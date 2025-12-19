using CAT.AID.Models;
using CAT.AID.Models.DTO;
using CAT.AID.Web.Data;
using CAT.AID.Web.Helpers;
using CAT.AID.Web.Models;
using CAT.AID.Web.Models.DTO;
using CAT.AID.Web.Services;
using CAT.AID.Web.Services.PDF;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using System.Text.Json;



namespace CAT.AID.Web.Controllers
{
    [Authorize]
    public class AssessmentsController : Controller
    {
        private readonly Dictionary<string, List<string>> _recommendationLibrary;

        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _user;
        private readonly IWebHostEnvironment _environment;

        public AssessmentsController(ApplicationDbContext db, UserManager<ApplicationUser> user, IWebHostEnvironment env)
        {
            _db = db;
            _user = user;
            _environment = env;
        }

        // -------------------- 1. TASKS FOR ASSESSOR --------------------
        [Authorize(Roles = "Assessor,LeadAssessor")]
        public async Task<IActionResult> MyTasks()
        {
            var userId = _user.GetUserId(User);

            IQueryable<Assessment> query = _db.Assessments
                .Include(a => a.Candidate);

            // Assessor sees only assigned tasks
            if (User.IsInRole("Assessor"))
            {
                query = query.Where(a => a.AssessorId == userId);
            }

            // Lead assessor sees all assigned & submitted
            if (User.IsInRole("LeadAssessor"))
            {
                query = query.Where(a =>
                    a.Status == AssessmentStatus.Assigned ||
                    a.Status == AssessmentStatus.Submitted);
            }

            var tasks = await query
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            if (!tasks.Any())
                return View(new List<CandidateAssessmentPivotVM>());

            var timestamps = tasks
                .Select(a => a.CreatedAt)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var grouped = tasks
                .GroupBy(a => a.CandidateId)
                .Select(g => new CandidateAssessmentPivotVM
                {
                    CandidateId = g.Key,
                    CandidateName = g.FirstOrDefault()?.Candidate?.FullName ?? "N/A",

                    AssessmentIds = timestamps.ToDictionary(
                        ts => ts,
                        ts => g.FirstOrDefault(a => a.CreatedAt == ts)?.Id
                    ),

                    StatusMapping = g
                        .Where(a => a != null)
                        .ToDictionary(
                            a => a.Id,
                            a => a.Status.ToString()
                        )
                })
                .ToList();

            ViewBag.Timestamps = timestamps;
            return View(grouped);
        }

        [Authorize(Roles = "LeadAssessor, Admin")]
        [HttpGet]
        public async Task<IActionResult> Compare(int candidateId, int[] ids)
        {
            if (ids == null || ids.Length < 2)
                return BadRequest("At least two assessments must be selected.");

            var assessments = await _db.Assessments
                .Include(a => a.Candidate)
                .Where(a => ids.Contains(a.Id))
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

            if (!assessments.Any())
                return NotFound();

            // Deserialize stored score JSON
            var scoreData = assessments
                .ToDictionary(a => a.Id, a =>
                    string.IsNullOrWhiteSpace(a.ScoreJson)
                    ? new AssessmentScoreDTO()
                    : System.Text.Json.JsonSerializer.Deserialize<AssessmentScoreDTO>(a.ScoreJson));

            ViewBag.Assessments = assessments;
            return View("CompareAssessments", scoreData);
        }

        // -------------------- 2. GET PERFORM ASSESSMENT --------------------
        [Authorize(Roles = "Assessor, LeadAssessor")]
        public async Task<IActionResult> Perform(int id)
        {
            var a = await _db.Assessments.Include(x => x.Candidate).FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return NotFound();

            var jsonPath = Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json");
            var sections = JsonSerializer.Deserialize<List<AssessmentSection>>(System.IO.File.ReadAllText(jsonPath));
            ViewBag.Sections = sections;

            return View(a);
        }
        // -------------------- 3. SUBMIT ASSESSMENT --------------------




        [HttpPost]
        [Authorize(Roles = "Assessor, LeadAssessor")]
        public async Task<IActionResult> Perform(int id, string actionType)
        {
            var assessment = await _db.Assessments
                .Include(a => a.Candidate)
                .FirstOrDefaultAsync(a => a.Id == id);

            if (assessment == null)
                return NotFound();

            if (!assessment.IsEditableByAssessor)
                return Unauthorized();

            // -----------------------------------------
            // 1️⃣ Collect all answers, scores & comments
            // -----------------------------------------
            var data = new Dictionary<string, string>();

            foreach (var key in Request.Form.Keys)
            {
                if (key.StartsWith("ANS_") ||
                    key.StartsWith("SCORE_") ||
                    key.StartsWith("CMT_") ||
                    key == "SUMMARY_COMMENTS")
                {
                    data[key] = Request.Form[key];
                }
            }

            // -----------------------------------------
            // 2️⃣ Handle FILE UPLOADS (PER QUESTION)
            // -----------------------------------------
            var uploadFolder = Path.Combine(_environment.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadFolder);

            var fileCounters = new Dictionary<string, int>();

            foreach (var file in Request.Form.Files)
            {
                if (file.Length == 0)
                    continue;

                // Expected: FILE_UPLOAD_{QuestionId}
                if (!file.Name.StartsWith("FILE_UPLOAD_"))
                    continue;

                var questionId = file.Name.Replace("FILE_UPLOAD_", "");

                if (!fileCounters.ContainsKey(questionId))
                    fileCounters[questionId] = 0;

                fileCounters[questionId]++;

                var savedFileName =
                    $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";

                var savePath = Path.Combine(uploadFolder, savedFileName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Persist reference in JSON
                data[$"FILE_{questionId}_{fileCounters[questionId]}"] = savedFileName;
            }

            // -----------------------------------------
            // 3️⃣ Save assessment result JSON
            // -----------------------------------------
            assessment.AssessmentResultJson =
                JsonSerializer.Serialize(data);

            // -----------------------------------------
            // 4️⃣ Calculate SCORE JSON
            // -----------------------------------------
            var sections = JsonSerializer.Deserialize<List<AssessmentSection>>(
                System.IO.File.ReadAllText(
                    Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json")
                )
            ) ?? new List<AssessmentSection>();

            var scoreDto = new AssessmentScoreDTO();
            int totalMaxScore = 0;

            foreach (var section in sections)
            {
                int sectionScore = 0;
                var questionScores = new Dictionary<string, int>();

                foreach (var q in section.Questions)
                {
                    totalMaxScore += 3;

                    var scoreKey = $"SCORE_{q.Id}";
                    if (data.TryGetValue(scoreKey, out var val) &&
                        int.TryParse(val, out int score))
                    {
                        sectionScore += score;
                        questionScores[q.Text] = score;
                    }
                }

                scoreDto.SectionScores[section.Category] = sectionScore;
                scoreDto.SectionQuestionScores[section.Category] = questionScores;
            }

            scoreDto.TotalScore = scoreDto.SectionScores.Sum(x => x.Value);
            scoreDto.MaxScore = totalMaxScore;

            assessment.ScoreJson =
                JsonSerializer.Serialize(scoreDto);

            // -----------------------------------------
            // 5️⃣ Handle SAVE / SUBMIT
            // -----------------------------------------
            if (actionType == "save")
            {
                assessment.Status = AssessmentStatus.InProgress;
                TempData["msg"] = "Assessment saved successfully.";
            }
            else if (actionType == "submit")
            {
                assessment.Status = AssessmentStatus.Submitted;
                assessment.SubmittedAt = DateTime.UtcNow;
                TempData["msg"] = "Assessment submitted for review.";
            }

            // -----------------------------------------
            // 6️⃣ Persist
            // -----------------------------------------
            await _db.SaveChangesAsync();

            return RedirectToAction(nameof(MyTasks));
        }


        public IActionResult Summary(int id)
        {
            var a = _db.Assessments
                .Include(a => a.Candidate)
                .Include(a => a.Assessor)
                .FirstOrDefault(a => a.Id == id);

            if (a == null) return NotFound();

            // Load Sections from DB
            var sections = JsonSerializer.Deserialize<List<AssessmentSection>>(
                System.IO.File.ReadAllText(
                    Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json")
                )
            ) ?? new List<AssessmentSection>();

            // Load Score JSON
            var score = JsonSerializer.Deserialize<AssessmentScoreDTO>(a.ScoreJson);

            // Load Recommendation Library
            var recFile = Path.Combine(_environment.WebRootPath, "data", "recommendations.json");
            var recommendationLibrary = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                System.IO.File.ReadAllText(recFile)
            );

            // Build MAX score table (each question max = 3)
            var sectionMaxScores = sections.ToDictionary(
                s => s.Category,
                s => s.Questions.Count * 3
            );

            // ---------------- Build Recommendation List ----------------
            Dictionary<string, List<string>> recommendations = new();
            Dictionary<string, List<(string Question, int Score)>> weakDetails = new();

            foreach (var sec in score.SectionScores)
            {
                double max = sectionMaxScores[sec.Key];
                double pct = (sec.Value / max) * 100;

                // Show recommendations only if performance < 100%
                if (pct < 100 && recommendationLibrary.ContainsKey(sec.Key))
                {
                    recommendations[sec.Key] = recommendationLibrary[sec.Key];
                }

                // Weak question breakdown
                var weakList = new List<(string Question, int Score)>();
                foreach (var q in sections.First(s => s.Category == sec.Key).Questions)
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(a.AssessmentResultJson);
                    saved.TryGetValue($"SCORE_{q.Id}", out string scr);
                    int sc = int.TryParse(scr, out int x) ? x : 0;

                    if (sc < 3)  // <3 means not fully achieved
                        weakList.Add((q.Text, sc));
                }

                if (weakList.Any())
                    weakDetails[sec.Key] = weakList;
            }

            // Send to UI
            ViewBag.Recommendations = recommendations;
            ViewBag.WeakDetails = weakDetails;
            ViewBag.Sections = sections;

            return View(a);
        }

        // -------------------- 4. SUMMARY RESULT DISPLAY --------------------
        [Authorize(Roles = "Assessor, LeadAssessor, Admin")]
        [Authorize]
        public async Task<IActionResult> ExportComparisonPdf(int candidateId, string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest("No assessments selected");

            var idList = ids
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToList();

            var assessments = await _db.Assessments
                .Include(a => a.Candidate)
                .Where(a => idList.Contains(a.Id))
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

            if (!assessments.Any())
                return NotFound();

            var model = ComparisonReportBuilder.Build(assessments);

            var document = new ComparisonPdfDocument(model);

            var pdfBytes = document.GeneratePdf();   // ✅ QuestPDF extension

            return File(
                pdfBytes,
                "application/pdf",
                $"Comparison_{model.CandidateName}.pdf"
            );
        }


        [Authorize]
        public async Task<IActionResult> ExportExcel(int id)
        {
            var a = await _db.Assessments.Include(x => x.Candidate).FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return NotFound();
            var file = ExcelGenerator.BuildScoreSheet(a);
            return File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Scores_{a.Id}.xlsx");
        }



        [Authorize(Roles = "Assessor, LeadAssessor, Admin")]
        public async Task<IActionResult> View(int id)
        {
            var assessment = await _db.Assessments
                .Include(a => a.Candidate)
                .Include(a => a.Assessor)
                .FirstOrDefaultAsync(a => a.Id == id);

            if (assessment == null)
                return NotFound();

            // ✅ Load ALL answers, summary & evidence
            var answers = string.IsNullOrWhiteSpace(assessment.AssessmentResultJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(assessment.AssessmentResultJson)!;

            ViewBag.Answers = answers;
            ViewBag.Summary = answers.GetValueOrDefault("SUMMARY_COMMENTS", "");

            // Load questions
            var qfile = Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json");
            var sections = JsonSerializer.Deserialize<List<AssessmentSection>>(
                System.IO.File.ReadAllText(qfile)
            ) ?? new List<AssessmentSection>();

            ViewBag.Sections = sections;

            return View("ViewAssessment", assessment);
        }

        [Authorize(Roles = "Assessor, LeadAssessor, Admin")]
        public async Task<IActionResult> Recommendations(int id)
        {
            var a = await _db.Assessments.Include(x => x.Candidate).FirstOrDefaultAsync(x => x.Id == id);
            if (a == null) return NotFound();

            var score = JsonSerializer.Deserialize<AssessmentScoreDTO>(a.ScoreJson);

            var mapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                System.IO.File.ReadAllText(Path.Combine(_environment.WebRootPath, "data", "recommendations.json"))
            );

            var result = new Dictionary<string, List<string>>();

            foreach (var sec in score.SectionScores)
            {
                double pct = (sec.Value / (score.MaxScore / score.SectionScores.Count)) * 100;
                if (pct < 60)  // Low area
                    result[sec.Key] = mapping[sec.Key];
            }

            ViewBag.Score = score;
            return View(result);
        }


        // -------------------- 6. GET REVIEW --------------------

        [Authorize(Roles = "LeadAssessor, Admin")]
        public async Task<IActionResult> Review(int id)
        {
            var assessment = await _db.Assessments
                .Include(a => a.Candidate)
                .Include(a => a.Assessor)
                .FirstOrDefaultAsync(a => a.Id == id);

            if (assessment == null)
                return NotFound();

            var answers = string.IsNullOrWhiteSpace(assessment.AssessmentResultJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(assessment.AssessmentResultJson)!;

            ViewBag.Answers = answers;
            ViewBag.Summary = answers.GetValueOrDefault("SUMMARY_COMMENTS", "");

            var qfile = Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json");
            ViewBag.Sections = JsonSerializer.Deserialize<List<AssessmentSection>>(
                System.IO.File.ReadAllText(qfile)
            ) ?? new List<AssessmentSection>();

            ViewBag.Assessors = await _db.Users
                .Where(u => u.Location == assessment.Candidate.CommunicationAddress)
                .ToListAsync();

            return View(assessment);
        }
        [Authorize]
        [HttpPost]
        public async Task<IActionResult> ExportReportPdf(int id)
        {
            var a = await _db.Assessments
                .Include(x => x.Candidate)
                .Include(x => x.Assessor)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (a == null) return NotFound();

            var score = string.IsNullOrWhiteSpace(a.ScoreJson)
                ? new AssessmentScoreDTO()
                : JsonSerializer.Deserialize<AssessmentScoreDTO>(a.ScoreJson);

            var qfile = Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json");
            var sections = JsonSerializer.Deserialize<List<AssessmentSection>>(System.IO.File.ReadAllText(qfile));

            var recFile = Path.Combine(_environment.WebRootPath, "data", "recommendations.json");
            var recommendations = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(System.IO.File.ReadAllText(recFile));

            // Capture charts generated from browser (POSTed from hidden inputs)
            string barChartRaw = Request.Form["barChartImage"];
            string doughnutChartRaw = Request.Form["doughnutChartImage"];

            byte[] barChart = Array.Empty<byte>();
            byte[] doughnutChart = Array.Empty<byte>();

            if (!string.IsNullOrWhiteSpace(barChartRaw) && barChartRaw.Contains(","))
            {
                barChartRaw = barChartRaw.Split(',')[1];   // remove mime header
                barChart = Convert.FromBase64String(barChartRaw);
            }

            if (!string.IsNullOrWhiteSpace(doughnutChartRaw) && doughnutChartRaw.Contains(","))
            {
                doughnutChartRaw = doughnutChartRaw.Split(',')[1];
                doughnutChart = Convert.FromBase64String(doughnutChartRaw);
            }

            var pdf = new FullAssessmentPdfService()
                .Generate(a, score, sections, recommendations, barChart, doughnutChart);

            return File(pdf, "application/pdf", $"Assessment_{a.Id}.pdf");
        }


        // -------------------- 7. POST REVIEW ACTION --------------------


        [HttpPost]
        [Authorize(Roles = "LeadAssessor, Admin")]
        public async Task<IActionResult> Review(
    int id,
    string leadComments,
    string action,
    string? newAssessorId)
        {
            var assessment = await _db.Assessments
                .Include(a => a.Candidate)
                .Include(a => a.Assessor)
                .FirstOrDefaultAsync(a => a.Id == id);

            if (assessment == null)
                return NotFound();

            // ✅ Enforce comments ONLY for Send Back / Reject
            if ((action == "sendback" || action == "reject") &&
                string.IsNullOrWhiteSpace(leadComments))
            {
                ModelState.AddModelError("LeadComments",
                    "Lead comments are mandatory for Send Back or Reject.");

                // reload data needed by Review view
                ViewBag.Sections = JsonSerializer.Deserialize<List<AssessmentSection>>(
                    System.IO.File.ReadAllText(
                        Path.Combine(_environment.WebRootPath, "data", "assessment_questions.json")
                    )
                );

                ViewBag.Assessors = await _db.Users.ToListAsync();

                return View(assessment); // 🔥 RETURN VIEW, NOT REDIRECT
            }

            // Save lead comments
            assessment.LeadComments = leadComments;
            assessment.ReviewedAt = DateTime.UtcNow;

            // Status change
            switch (action)
            {
                case "approve":
                    assessment.Status = AssessmentStatus.Approved;
                    break;

                case "sendback":
                    assessment.Status = AssessmentStatus.SentBack;
                    break;

                case "reject":
                    assessment.Status = AssessmentStatus.Rejected;
                    break;
            }

            // Reassign assessor if selected
            if (!string.IsNullOrWhiteSpace(newAssessorId))
            {
                assessment.AssessorId = newAssessorId;
                assessment.Status = AssessmentStatus.Assigned;
            }

            await _db.SaveChangesAsync();

            TempData["msg"] = $"Assessment {action} successfully.";
            return RedirectToAction(nameof(ReviewQueue));
        }

        [Authorize(Roles = "LeadAssessor, Admin")]
        public async Task<IActionResult> ReviewQueue()
        {
            var list = await _db.Assessments
                .Include(a => a.Candidate)
                .Where(a => a.Status == AssessmentStatus.Submitted)
                .OrderByDescending(a => a.Id)
                .ToListAsync();

            return View(list);
        }


        [Authorize(Roles = "LeadAssessor, Admin")]
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, string action)
        {
            var a = await _db.Assessments.FindAsync(id);
            if (a == null) return NotFound();

            if (action == "approve")
                a.Status = AssessmentStatus.Approved;

            else if (action == "reject")
                a.Status = AssessmentStatus.Rejected;

            else if (action == "sendback")
                a.Status = AssessmentStatus.SentBack;

            a.ReviewedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["msg"] = $"Assessment {action} successful!";
            return RedirectToAction(nameof(ReviewQueue));
        }


        [Authorize(Roles = "LeadAssessor, Admin")]

        // -------------------- 8. HISTORY --------------------N
        public async Task<IActionResult> History(int candidateId)
        {
            var list = await _db.Assessments
                .Include(a => a.Candidate)
                .Where(a => a.CandidateId == candidateId)
                .OrderByDescending(a => a.Id)
                .ToListAsync();
            return View(list);
        }
    }
}











