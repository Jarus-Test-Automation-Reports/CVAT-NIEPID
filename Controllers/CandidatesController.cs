using CAT.AID.Models;
using CAT.AID.Models.DTO;
using CAT.AID.Web.Data;
using CAT.AID.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ComparisonReportVM = CAT.AID.Web.Models.DTO.ComparisonReportVM;

namespace CAT.AID.Web.Controllers
{
    [Authorize(Roles = "LeadAssessor")]
    public class CandidatesController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _env;

        public CandidatesController(
            ApplicationDbContext db,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment env)
        {
            _db = db;
            _userManager = userManager;
            _env = env;
        }

        private string UploadRoot =>
            Path.Combine(_env.WebRootPath, "uploads/candidates");

        // ---------------------------------------------------------
        // DETAILS
        // ---------------------------------------------------------
        public async Task<IActionResult> Details(int id)
        {
            var candidate = await _db.Candidates.FindAsync(id);
            if (candidate == null) return NotFound();

            ViewBag.Attachments = await _db.CandidateAttachments
                .Where(a => a.CandidateId == id)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync();

            return View(candidate);
        }

        // ---------------------------------------------------------
        // INDEX
        // ---------------------------------------------------------
        public async Task<IActionResult> Index()
        {
            return View(await _db.Candidates
                .Where(x => !x.IsArchived)
                .OrderByDescending(x => x.Id)
                .ToListAsync());
        }

        // ---------------------------------------------------------
        // CREATE (GET)
        // ---------------------------------------------------------
        public IActionResult Create()
        {
            return View(new Candidate { DOB = DateTime.Today });
        }

        // ---------------------------------------------------------
        // CREATE (POST)
        // ---------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            Candidate model,
            IFormFile? PhotoFile,
            List<IFormFile>? AttachmentFiles)
        {
            if (!ModelState.IsValid)
                return View(model);

            Directory.CreateDirectory(UploadRoot);

            // PHOTO SAVE
            if (PhotoFile != null && PhotoFile.Length > 0)
            {
                var name = $"{Guid.NewGuid()}{Path.GetExtension(PhotoFile.FileName)}";
                var path = Path.Combine(UploadRoot, name);

                using var stream = new FileStream(path, FileMode.Create);
                await PhotoFile.CopyToAsync(stream);

                model.PhotoFileName = name;
                model.PhotoFilePath = "/uploads/candidates/" + name;
            }

            // PostgreSQL requires UTC
            model.DOB = DateTime.SpecifyKind(model.DOB, DateTimeKind.Utc);

            _db.Candidates.Add(model);
            await _db.SaveChangesAsync();

            // Attachments added dynamically
            if (AttachmentFiles != null)
                await SaveAttachments(model.Id, AttachmentFiles);

            TempData["msg"] = "Candidate created successfully.";
            return RedirectToAction(nameof(Index));
        }

        // ---------------------------------------------------------
        // EDIT (GET)
        // ---------------------------------------------------------
        public async Task<IActionResult> Edit(int id)
        {
            var candidate = await _db.Candidates.FindAsync(id);
            if (candidate == null) return NotFound();

            ViewBag.Attachments = await _db.CandidateAttachments
                .Where(a => a.CandidateId == id)
                .ToListAsync();

            return View(candidate);
        }

        // ---------------------------------------------------------
        // EDIT (POST)
        // ---------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            Candidate model,
            IFormFile? PhotoFile,
            List<IFormFile>? AttachmentFiles)
        {
            var dbModel = await _db.Candidates.FindAsync(id);
            if (dbModel == null) return NotFound();

            if (!ModelState.IsValid)
            {
                ViewBag.Attachments = await _db.CandidateAttachments
                    .Where(a => a.CandidateId == id)
                    .ToListAsync();
                return View(model);
            }

            Directory.CreateDirectory(UploadRoot);

            // ---------------- PHOTO UPDATE ----------------
            if (PhotoFile != null && PhotoFile.Length > 0)
            {
                if (!string.IsNullOrEmpty(dbModel.PhotoFileName))
                {
                    var oldPath = Path.Combine(UploadRoot, dbModel.PhotoFileName);
                    if (System.IO.File.Exists(oldPath))
                        System.IO.File.Delete(oldPath);
                }

                var name = $"{Guid.NewGuid()}{Path.GetExtension(PhotoFile.FileName)}";
                var newPath = Path.Combine(UploadRoot, name);

                using var stream = new FileStream(newPath, FileMode.Create);
                await PhotoFile.CopyToAsync(stream);

                dbModel.PhotoFileName = name;
                dbModel.PhotoFilePath = "/uploads/candidates/" + name;
            }

            // ---------------- UPDATE PERSONAL FIELDS ----------------
            dbModel.FullName = model.FullName;
            dbModel.Gender = model.Gender;
            dbModel.DOB = DateTime.SpecifyKind(model.DOB, DateTimeKind.Utc);
            dbModel.IntellectualLevel = model.IntellectualLevel;
            dbModel.MaritalStatus = model.MaritalStatus;
            dbModel.Education = model.Education;

            // Parent
            dbModel.FatherName = model.FatherName;
            dbModel.FatherEducation = model.FatherEducation;
            dbModel.FatherOccupation = model.FatherOccupation;

            dbModel.MotherName = model.MotherName;
            dbModel.MotherEducation = model.MotherEducation;
            dbModel.MotherOccupation = model.MotherOccupation;

            dbModel.MotherTongue = model.MotherTongue;
            dbModel.OtherLanguages = model.OtherLanguages;

            // Family
            dbModel.FamilyType = model.FamilyType;
            dbModel.FamilyDisabilityHistory = model.FamilyDisabilityHistory;
            dbModel.DisabilityType = model.DisabilityType;
            dbModel.MonthlyIncome = model.MonthlyIncome;

            dbModel.ResidentialArea = model.ResidentialArea;
            dbModel.ContactNumber = model.ContactNumber;
            dbModel.CommunicationAddress = model.CommunicationAddress;

            await _db.SaveChangesAsync();

            // NEW ATTACHMENTS
            if (AttachmentFiles != null)
                await SaveAttachments(id, AttachmentFiles);

            TempData["msg"] = "Candidate updated successfully.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // ---------------------------------------------------------
        // DELETE ATTACHMENT
        // ---------------------------------------------------------
        [HttpPost]
        public async Task<IActionResult> DeleteAttachment(int id)
        {
            var file = await _db.CandidateAttachments.FindAsync(id);
            if (file == null) return NotFound();

var fullPath = Path.Combine(
    _env.WebRootPath,
    file.FilePath.TrimStart('/')
);
            if (System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);

            _db.CandidateAttachments.Remove(file);
            await _db.SaveChangesAsync();

            TempData["msg"] = "Attachment deleted.";
            return RedirectToAction(nameof(Edit), new { id = file.CandidateId });
        }

        // ---------------------------------------------------------
        // ARCHIVE
        // ---------------------------------------------------------
        public async Task<IActionResult> Archive(int id)
        {
            var m = await _db.Candidates.FindAsync(id);
            if (m == null) return NotFound();

            m.IsArchived = true;
            await _db.SaveChangesAsync();

            TempData["msg"] = "Candidate archived.";
            return RedirectToAction(nameof(Index));
        }

        // ---------------------------------------------------------
        // ASSIGN (GET)
        // ---------------------------------------------------------
        public async Task<IActionResult> Assign(int id)
        {
            var candidate = await _db.Candidates.FindAsync(id);
            if (candidate == null) return NotFound();

            var assessors = await _userManager.GetUsersInRoleAsync("Assessor");

            var filtered = assessors
                .Where(x => x.Location == candidate.CommunicationAddress)
                .ToList();

            if (!filtered.Any())
                filtered = assessors.ToList();

            ViewBag.Assessors = filtered;
            ViewBag.Candidate = candidate;

            return View();
        }

        // ---------------------------------------------------------
        // ASSIGN (POST)
        // ---------------------------------------------------------
        [HttpPost]
        public async Task<IActionResult> Assign(int id, string assessorId)
        {
            var candidate = await _db.Candidates.FindAsync(id);
            if (candidate == null) return NotFound();

            if (string.IsNullOrWhiteSpace(assessorId))
                return BadRequest("Invalid assessor id");

            var leadId = _userManager.GetUserId(User);

            var assessment = new Assessment
            {
                CandidateId = id,
                AssessorId = assessorId,
                LeadAssessorId = leadId,
                Status = AssessmentStatus.Assigned,
                CreatedAt = DateTime.UtcNow
            };

            _db.Assessments.Add(assessment);
            await _db.SaveChangesAsync();

            TempData["msg"] = "Assessor assigned successfully!";
            return RedirectToAction(nameof(Index));
        }

        // ---------------------------------------------------------
        // COMPARE ASSESSMENTS
        // ---------------------------------------------------------
        public async Task<IActionResult> Compare(int candidateId, List<int> ids)
        {
            if (candidateId == 0 || ids.Count < 2)
            {
                TempData["msg"] = "Select at least two assessments.";
                return RedirectToAction("MyTasks", "Assessments");
            }

            var candidate = await _db.Candidates.FindAsync(candidateId);
            if (candidate == null)
            {
                TempData["msg"] = "Candidate not found.";
                return RedirectToAction("MyTasks", "Assessments");
            }

            var assessments = await _db.Assessments
                .Where(a => ids.Contains(a.Id) &&
                            a.CandidateId == candidateId &&
                            (a.Status == AssessmentStatus.Approved ||
                             a.Status == AssessmentStatus.Submitted))
                .OrderBy(a => a.SubmittedAt)
                .ToListAsync();

            if (assessments.Count < 2)
            {
                TempData["msg"] = "No comparable assessments.";
                return RedirectToAction("MyTasks", "Assessments");
            }

            var vm = new ComparisonReportVM
            {
                CandidateId = candidateId,
                CandidateName = candidate.FullName,
                AssessmentIds = ids,
                Assessments = assessments
            };

            return View("CompareReport", vm);
        }

        // ---------------------------------------------------------
        // SAVE ATTACHMENTS (shared helper)
        // ---------------------------------------------------------
        private async Task SaveAttachments(int candidateId, List<IFormFile> files)
        {
            Directory.CreateDirectory(UploadRoot);

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                var name = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(UploadRoot, name);

                using var stream = new FileStream(filePath, FileMode.Create);
                await file.CopyToAsync(stream);

                _db.CandidateAttachments.Add(new CandidateAttachment
                {
                    CandidateId = candidateId,
                    FileName = file.FileName,
FilePath = "/uploads/candidates/" + name,
                    FileType = file.ContentType,
                    UploadedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }
    }
}


