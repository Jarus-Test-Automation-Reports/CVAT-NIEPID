using CAT.AID.Models;
using CAT.AID.Models.DTO;
using CAT.AID.Web.Data;
using CAT.AID.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
            Path.Combine(_env.WebRootPath, "uploads", "candidates");

        // ---------------- EDIT (GET) ----------------
        public async Task<IActionResult> Edit(int id)
        {
            var candidate = await _db.Candidates.FindAsync(id);
            if (candidate == null) return NotFound();

            ViewBag.Attachments = await _db.CandidateAttachments
                .Where(a => a.CandidateId == id)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync();

            return View(candidate);
        }

        // ---------------- EDIT (POST) ----------------
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

            // ---------- PHOTO ----------
            if (PhotoFile != null && PhotoFile.Length > 0)
            {
                var name = $"{Guid.NewGuid()}{Path.GetExtension(PhotoFile.FileName)}";
                var path = Path.Combine(UploadRoot, name);

                using var stream = new FileStream(path, FileMode.Create);
                await PhotoFile.CopyToAsync(stream);

                dbModel.PhotoFileName = name;
                dbModel.PhotoFilePath = "/uploads/candidates/" + name;
            }

            // ---------- UPDATE FIELDS ----------
            dbModel.FullName = model.FullName;
            dbModel.Gender = model.Gender;
            dbModel.DOB = DateTime.SpecifyKind(model.DOB, DateTimeKind.Utc);
            dbModel.IntellectualLevel = model.IntellectualLevel;
            dbModel.MaritalStatus = model.MaritalStatus;
            dbModel.Education = model.Education;
            dbModel.FatherName = model.FatherName;
            dbModel.FatherEducation = model.FatherEducation;
            dbModel.FatherOccupation = model.FatherOccupation;
            dbModel.MotherName = model.MotherName;
            dbModel.MotherEducation = model.MotherEducation;
            dbModel.MotherOccupation = model.MotherOccupation;
            dbModel.MotherTongue = model.MotherTongue;
            dbModel.OtherLanguages = model.OtherLanguages;
            dbModel.FamilyType = model.FamilyType;
            dbModel.FamilyDisabilityHistory = model.FamilyDisabilityHistory;
            dbModel.DisabilityType = model.DisabilityType;
            dbModel.MonthlyIncome = model.MonthlyIncome;
            dbModel.ResidentialArea = model.ResidentialArea;
            dbModel.ContactNumber = model.ContactNumber;
            dbModel.CommunicationAddress = model.CommunicationAddress;

            await _db.SaveChangesAsync();

            // ---------- DOCUMENTS ----------
            if (AttachmentFiles != null && AttachmentFiles.Any())
                await SaveAttachments(id, AttachmentFiles);

            TempData["msg"] = "Candidate updated successfully.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // ---------------- DELETE ATTACHMENT ----------------
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

        // ---------------- SAVE ATTACHMENTS ----------------
        private async Task SaveAttachments(int candidateId, List<IFormFile> files)
        {
            Directory.CreateDirectory(UploadRoot);

            foreach (var file in files)
            {
                if (file == null || file.Length == 0) continue;

                var name = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var fullPath = Path.Combine(UploadRoot, name);

                using var stream = new FileStream(fullPath, FileMode.Create);
                await file.CopyToAsync(stream);

                _db.CandidateAttachments.Add(new CandidateAttachment
                {
                    CandidateId = candidateId,
                    FileName = file.FileName,
                    FilePath = "/uploads/candidates/" + name, // ✅ IMPORTANT
                    FileType = file.ContentType,
                    UploadedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }
    }
}
