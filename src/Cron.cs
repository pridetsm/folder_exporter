// Cron.cs - 5-field cron expression matching (minute hour day-of-month month
// day-of-week), evaluated against the current local minute each poll tick -
// the same way cron(8) itself works, rather than by precomputing "next fire
// time" arithmetic.
//
// That choice sidesteps DST entirely: a wall-clock minute that never occurs
// (the spring-forward gap) is simply never "now", so the job is silently
// skipped that one day; a minute that occurs twice (the fall-back overlap)
// can match twice, exactly as it would on a Linux box running real cron.
// Scheduler.cs is what prevents double-firing in the normal case, by
// advancing past every minute it has already evaluated.
using System;
using System.Globalization;

namespace FolderExporter
{
    internal sealed class CronSchedule
    {
        public readonly string Source;

        private readonly bool[] _minute = new bool[60];
        private readonly bool[] _hour = new bool[24];
        private readonly bool[] _day = new bool[32];    // index 1..31
        private readonly bool[] _month = new bool[13];  // index 1..12
        private readonly bool[] _dow = new bool[7];      // index 0..6, 0 = Sunday
        private readonly bool _restrictedDay;
        private readonly bool _restrictedDow;

        private CronSchedule(string source, bool restrictedDay, bool restrictedDow)
        {
            Source = source;
            _restrictedDay = restrictedDay;
            _restrictedDow = restrictedDow;
        }

        public static void Validate(string expr) { Parse(expr); }

        public static CronSchedule Parse(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) throw new Exception("empty cron expression");
            string[] p = expr.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 5)
                throw new Exception("expected 5 fields (minute hour day month weekday), found " + p.Length);

            bool restrictedDay = p[2].Trim() != "*";
            bool restrictedDow = p[4].Trim() != "*";

            var c = new CronSchedule(expr.Trim(), restrictedDay, restrictedDow);
            FillField(c._minute, p[0], 0, 59, "minute");
            FillField(c._hour, p[1], 0, 23, "hour");
            FillField(c._day, p[2], 1, 31, "day of month");
            FillField(c._month, p[3], 1, 12, "month");

            var dow = new bool[8];
            FillField(dow, p[4], 0, 7, "day of week");
            if (dow[7]) dow[0] = true;   // both 0 and 7 mean Sunday
            Array.Copy(dow, c._dow, 7);

            return c;
        }

        /// <summary>True if this local (wall-clock) minute matches the expression.</summary>
        public bool Matches(DateTime local)
        {
            if (!_minute[local.Minute]) return false;
            if (!_hour[local.Hour]) return false;
            if (!_month[local.Month]) return false;

            bool dayOk = _day[local.Day];
            bool dowOk = _dow[(int)local.DayOfWeek];

            // Standard cron rule: when BOTH day-of-month and day-of-week are
            // restricted (neither is "*"), a match on either one is enough.
            // When only one is restricted, both must agree - which is automatic
            // since the unrestricted field is true for every value.
            if (_restrictedDay && _restrictedDow) return dayOk || dowOk;
            return dayOk && dowOk;
        }

        private static void FillField(bool[] slots, string field, int min, int max, string label)
        {
            foreach (string term in field.Split(','))
            {
                string t = term.Trim();
                if (t.Length == 0) throw new Exception("empty term in " + label + " field");

                int step = 1;
                string range = t;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    range = t.Substring(0, slash);
                    string stepStr = t.Substring(slash + 1);
                    if (!int.TryParse(stepStr, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                        throw new Exception("invalid step \"" + stepStr + "\" in " + label + " field");
                }

                int lo, hi;
                if (range == "*")
                {
                    lo = min; hi = max;
                }
                else
                {
                    int dash = range.IndexOf('-');
                    if (dash > 0)
                    {
                        if (!int.TryParse(range.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out lo) ||
                            !int.TryParse(range.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi))
                            throw new Exception("invalid range \"" + range + "\" in " + label + " field");
                    }
                    else
                    {
                        if (!int.TryParse(range, NumberStyles.Integer, CultureInfo.InvariantCulture, out lo))
                            throw new Exception("invalid value \"" + range + "\" in " + label + " field");
                        hi = lo;
                    }
                }

                if (lo < min || hi > max || lo > hi)
                    throw new Exception("\"" + t + "\" out of range for " + label + " (" + min + "-" + max + ")");

                for (int v = lo; v <= hi; v += step) slots[v] = true;
            }
        }
    }
}
