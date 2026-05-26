using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Globalization;

namespace Ams.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AttendanceController : ControllerBase
    {
        private readonly AppDbContext _context;

        public AttendanceController(AppDbContext context)
        {
            _context = context;
        }

        public class ClockRequest
        {
            [JsonPropertyName("userId")]
            public int UserId { get; set; }
        }

        // Parses time strings in both "h:mm tt" and "hh:mm tt" formats
        private static bool TryParseTime(string timeStr, out DateTime result)
        {
            string[] formats = { "h:mm tt", "hh:mm tt", "H:mm", "HH:mm" };
            return DateTime.TryParseExact(
                timeStr,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out result
            );
        }

        [HttpPost("clock-in")]
        public async Task<IActionResult> ClockIn([FromBody] ClockRequest request)
        {
            if (request == null || request.UserId <= 0)
            {
                return BadRequest(new { error = "Invalid User ID." });
            }

            var user = await _context.Users
                .Include(u => u.Shift)
                .FirstOrDefaultAsync(u => u.Id == request.UserId);

            if (user == null)
            {
                return NotFound(new { error = "User not found." });
            }

            string todayStr = DateTime.Now.ToString("yyyy-MM-dd");

            // Check if already clocked in today (active or already checked out)
            var existingLog = await _context.AttendanceLogs
                .FirstOrDefaultAsync(l => l.UserId == request.UserId && l.Date == todayStr && l.Status != "On Leave");

            if (existingLog != null)
            {
                return BadRequest(new { error = "You have already logged attendance for today." });
            }

            // Use single-digit hour format for cleaner display (e.g. "9:05 AM")
            string clockInTime = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);

            bool isLate = false;
            if (user.Shift != null && TryParseTime(user.Shift.StartTime, out DateTime shiftStart) && TryParseTime(clockInTime, out DateTime parsedClockIn))
            {
                // Align dates
                shiftStart = new DateTime(parsedClockIn.Year, parsedClockIn.Month, parsedClockIn.Day, shiftStart.Hour, shiftStart.Minute, 0);
                if (parsedClockIn > shiftStart.AddMinutes(user.Shift.GraceTime))
                {
                    isLate = true;
                }
            }

            var log = new AttendanceLog
            {
                UserId = request.UserId,
                Date = todayStr,
                ClockIn = clockInTime,
                ClockOut = "---",
                Status = "Active",
                Hours = 0.0,
                LateEntry = isLate
            };

            _context.AttendanceLogs.Add(log);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Clock-in successful!", log = log });
        }

        [HttpPost("clock-out")]
        public async Task<IActionResult> ClockOut([FromBody] ClockRequest request)
        {
            if (request == null || request.UserId <= 0)
            {
                return BadRequest(new { error = "Invalid User ID." });
            }

            // Find current active clock-in log in database
            var log = await _context.AttendanceLogs
                .Include(l => l.User)
                .ThenInclude(u => u.Shift)
                .FirstOrDefaultAsync(l => l.UserId == request.UserId && l.Status == "Active");

            if (log == null)
            {
                return BadRequest(new { error = "No active clock-in session found." });
            }

            var user = log.User;

            string clockOutTime = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);

            // Accurate work hours calculation — supports multiple time formats + midnight crossing
            double actualHours = 0.0;
            double overtimeHours = 0.0;
            double pendingHours = 0.0;
            bool isEarly = false;
            double breakDuration = 0.0;

            if (TryParseTime(log.ClockIn, out DateTime parsedClockIn) &&
                TryParseTime(clockOutTime, out DateTime parsedClockOut))
            {
                // If clock-out is before clock-in, employee worked past midnight — add 1 day
                if (parsedClockOut < parsedClockIn)
                {
                    parsedClockOut = parsedClockOut.AddDays(1);
                }

                TimeSpan duration = parsedClockOut - parsedClockIn;
                actualHours = Math.Round(duration.TotalHours, 2);

                if (user?.Shift != null)
                {
                    breakDuration = Math.Round(user.Shift.BreakTime / 60.0, 2);
                    actualHours -= breakDuration;
                    if (actualHours < 0) actualHours = 0;

                    if (TryParseTime(user.Shift.EndTime, out DateTime shiftEnd) && TryParseTime(user.Shift.StartTime, out DateTime shiftStart))
                    {
                        shiftStart = new DateTime(parsedClockIn.Year, parsedClockIn.Month, parsedClockIn.Day, shiftStart.Hour, shiftStart.Minute, 0);
                        shiftEnd = new DateTime(parsedClockIn.Year, parsedClockIn.Month, parsedClockIn.Day, shiftEnd.Hour, shiftEnd.Minute, 0);
                        
                        if (shiftEnd < shiftStart) shiftEnd = shiftEnd.AddDays(1);

                        double requiredHours = (shiftEnd - shiftStart).TotalHours - breakDuration;
                        if (requiredHours < 0) requiredHours = 0;

                        if (actualHours > requiredHours)
                        {
                            overtimeHours = Math.Round(actualHours - requiredHours, 2);
                        }
                        else
                        {
                            pendingHours = Math.Round(requiredHours - actualHours, 2);
                        }

                        // Align clock out date with shift end date for early exit check
                        DateTime adjustedClockOut = new DateTime(parsedClockOut.Year, parsedClockOut.Month, parsedClockOut.Day, parsedClockOut.Hour, parsedClockOut.Minute, 0);
                        if (adjustedClockOut < shiftEnd)
                        {
                            isEarly = true;
                        }
                    }
                }
                else
                {
                    if (actualHours < 0) actualHours = 0;
                }
            }

            log.ClockOut = clockOutTime;
            log.Status = "Present";
            log.Hours = actualHours;
            log.EarlyExit = isEarly;
            log.OvertimeHours = overtimeHours;
            log.PendingHours = pendingHours;
            log.BreakDuration = breakDuration;

            // Increment employee metrics in SQL Server
            if (user != null)
            {
                user.PresentDays += 1;
                user.WorkHoursThisMonth = Math.Round(user.WorkHoursThisMonth + actualHours, 2);

                // Recompute attendance rate
                int totalDays = user.PresentDays + user.AbsentDays;
                if (totalDays > 0)
                {
                    user.AttendanceRate = Math.Round(((double)user.PresentDays / totalDays) * 100.0, 1);
                }
            }



            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Clock-out successful!",
                log = new
                {
                    id = log.Id,
                    date = log.Date,
                    clockIn = log.ClockIn,
                    clockOut = log.ClockOut,
                    status = log.Status,
                    hours = log.Hours
                },
                user = user == null ? null : new
                {
                    presentDays = user.PresentDays,
                    workHoursThisMonth = user.WorkHoursThisMonth,
                    attendanceRate = user.AttendanceRate
                }
            });
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetAttendanceHistory()
        {
            var logs = await _context.AttendanceLogs
                .Include(l => l.User)
                .ThenInclude(u => u.Shift)
                .OrderByDescending(l => l.Date)
                .Select(l => new {
                    id = l.Id,
                    userId = l.UserId,
                    userName = l.User != null ? l.User.Name : "Unknown",
                    department = l.User != null ? l.User.Department : "Unknown",
                    date = l.Date,
                    clockIn = l.ClockIn,
                    clockOut = l.ClockOut,
                    status = l.Status,
                    hours = l.Hours,
                    lateEntry = l.LateEntry,
                    earlyExit = l.EarlyExit,
                    overtimeHours = l.OvertimeHours,
                    pendingHours = l.PendingHours,
                    breakDuration = l.BreakDuration,
                    shiftName = l.User != null && l.User.Shift != null ? l.User.Shift.Name : "None",
                    shiftStart = l.User != null && l.User.Shift != null ? l.User.Shift.StartTime : "",
                    shiftEnd = l.User != null && l.User.Shift != null ? l.User.Shift.EndTime : ""
                })
                .ToListAsync();

            return Ok(logs);
        }
    }
}
