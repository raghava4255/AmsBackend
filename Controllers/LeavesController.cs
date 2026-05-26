using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace Ams.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LeavesController : ControllerBase
    {
        private readonly AppDbContext _context;

        public LeavesController(AppDbContext context)
        {
            _context = context;
        }

        public class LeaveSubmission
        {
            [JsonPropertyName("userId")]
            public int UserId { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; } = string.Empty;

            [JsonPropertyName("duration")]
            public string Duration { get; set; } = string.Empty;

            [JsonPropertyName("reason")]
            public string Reason { get; set; } = string.Empty;
        }

        public class ResolveRequest
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("decision")]
            public string Decision { get; set; } = string.Empty; // "approve" or "reject"
        }

        // GET /api/leaves/user/{userId} — Employee fetches their own leave history
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetUserLeaves(int userId)
        {
            if (userId <= 0)
            {
                return BadRequest(new { error = "Invalid User ID." });
            }

            var leaves = await _context.LeaveRequests
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.Id)
                .Select(r => new
                {
                    id = r.Id,
                    type = r.Type,
                    duration = r.Duration,
                    reason = r.Reason,
                    status = r.Status
                })
                .ToListAsync();

            return Ok(new { leaves = leaves });
        }

        [HttpPost("request")]
        public async Task<IActionResult> RequestLeave([FromBody] LeaveSubmission request)
        {
            if (request == null || request.UserId <= 0 || string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Duration))
            {
                return BadRequest(new { error = "Invalid leave request parameters." });
            }

            var user = await _context.Users.FindAsync(request.UserId);
            if (user == null)
            {
                return NotFound(new { error = "User not found." });
            }

            var leave = new LeaveRequest
            {
                UserId = request.UserId,
                Name = user.Name,
                Type = request.Type,
                Duration = request.Duration,
                Reason = request.Reason,
                Status = "Pending"
            };

            _context.LeaveRequests.Add(leave);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Leave request submitted successfully!",
                leave = new
                {
                    id = leave.Id,
                    type = leave.Type,
                    duration = leave.Duration,
                    reason = leave.Reason,
                    status = leave.Status
                }
            });
        }

        [HttpPost("resolve")]
        public async Task<IActionResult> ResolveLeave([FromBody] ResolveRequest request)
        {
            if (request == null || request.Id <= 0 || string.IsNullOrWhiteSpace(request.Decision))
            {
                return BadRequest(new { error = "Invalid resolution parameters." });
            }

            var leave = await _context.LeaveRequests.FindAsync(request.Id);
            if (leave == null)
            {
                return NotFound(new { error = "Leave request not found." });
            }

            if (leave.Status != "Pending")
            {
                return BadRequest(new { error = "This request has already been resolved." });
            }

            string normalizedDecision = request.Decision.Trim().ToLower();
            if (normalizedDecision == "approve")
            {
                leave.Status = "Approved";

                // Find the employee and decrement their leave balance
                var employee = await _context.Users.FindAsync(leave.UserId);
                if (employee != null)
                {
                    // Estimate days from duration string
                    int daysToDeduct = 1;
                    if (leave.Duration.Contains("2")) daysToDeduct = 2;
                    else if (leave.Duration.Contains("3")) daysToDeduct = 3;
                    else if (leave.Duration.Contains("4")) daysToDeduct = 4;
                    else if (leave.Duration.Contains("5")) daysToDeduct = 5;

                    employee.LeaveBalance = Math.Max(0, employee.LeaveBalance - daysToDeduct);

                    // Add "On Leave" attendance entry for the approval date
                    _context.AttendanceLogs.Add(new AttendanceLog
                    {
                        UserId = employee.Id,
                        Date = DateTime.Now.ToString("yyyy-MM-dd"),
                        ClockIn = "---",
                        ClockOut = "---",
                        Status = "On Leave",
                        Hours = 0.0
                    });
                }
            }
            else if (normalizedDecision == "reject")
            {
                leave.Status = "Rejected";
            }
            else
            {
                return BadRequest(new { error = "Decision must be 'approve' or 'reject'." });
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = $"Leave request successfully {leave.Status}!",
                leave = new
                {
                    id = leave.Id,
                    type = leave.Type,
                    duration = leave.Duration,
                    reason = leave.Reason,
                    status = leave.Status
                }
            });
        }
    }
}
