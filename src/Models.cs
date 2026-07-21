using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodexQuotaTray
{
    internal sealed class QuotaWindowInfo
    {
        public int UsedPercent { get; set; }
        public long? WindowMinutes { get; set; }
        public long? ResetsAtUnix { get; set; }

        public int RemainingPercent
        {
            get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); }
        }

        public DateTime? ResetLocalTime
        {
            get
            {
                if (!ResetsAtUnix.HasValue)
                {
                    return null;
                }

                try
                {
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(ResetsAtUnix.Value)
                        .ToLocalTime();
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    internal sealed class SpendLimitInfo
    {
        public string Limit { get; set; }
        public string Used { get; set; }
        public int RemainingPercent { get; set; }
        public long? ResetsAtUnix { get; set; }
    }

    internal sealed class QuotaBucketInfo
    {
        public string LimitId { get; set; }
        public string LimitName { get; set; }
        public QuotaWindowInfo Primary { get; set; }
        public QuotaWindowInfo Secondary { get; set; }
        public SpendLimitInfo IndividualLimit { get; set; }
        public bool CreditsUnlimited { get; set; }
        public string CreditBalance { get; set; }

        public string DisplayName
        {
            get
            {
                if (!String.IsNullOrEmpty(LimitName))
                {
                    return LimitName;
                }

                return String.IsNullOrEmpty(LimitId) ? "其它额度" : LimitId;
            }
        }

        public int? DisplayRemainingPercent
        {
            get
            {
                List<int> values = new List<int>();
                if (Primary != null) values.Add(Primary.RemainingPercent);
                if (Secondary != null) values.Add(Secondary.RemainingPercent);
                if (IndividualLimit != null)
                {
                    values.Add(Math.Max(0, Math.Min(100, IndividualLimit.RemainingPercent)));
                }

                if (values.Count == 0) return null;
                int minimum = values[0];
                for (int index = 1; index < values.Count; index++)
                {
                    minimum = Math.Min(minimum, values[index]);
                }

                return minimum;
            }
        }

        public bool IsUnlimited
        {
            get { return !DisplayRemainingPercent.HasValue && CreditsUnlimited; }
        }

        public static QuotaBucketInfo FromSnapshot(QuotaSnapshot snapshot)
        {
            QuotaBucketInfo bucket = new QuotaBucketInfo();
            bucket.LimitId = snapshot.LimitId;
            bucket.LimitName = snapshot.LimitName;
            bucket.Primary = snapshot.Primary;
            bucket.Secondary = snapshot.Secondary;
            bucket.IndividualLimit = snapshot.IndividualLimit;
            bucket.CreditsUnlimited = snapshot.CreditsUnlimited;
            bucket.CreditBalance = snapshot.CreditBalance;
            return bucket;
        }
    }

    internal sealed class QuotaSnapshot
    {
        public string LimitId { get; set; }
        public string LimitName { get; set; }
        public QuotaWindowInfo Primary { get; set; }
        public QuotaWindowInfo Secondary { get; set; }
        public SpendLimitInfo IndividualLimit { get; set; }
        public bool HasCredits { get; set; }
        public bool CreditsUnlimited { get; set; }
        public string CreditBalance { get; set; }
        public string PlanType { get; set; }
        public string RateLimitReachedType { get; set; }
        public int? ResetCreditCount { get; set; }
        public List<QuotaBucketInfo> AdditionalBuckets { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        public string SourceName { get; set; }
        public bool IsFallback { get; set; }

        public int? DisplayRemainingPercent
        {
            get
            {
                List<int> values = new List<int>();
                if (Primary != null)
                {
                    values.Add(Primary.RemainingPercent);
                }

                if (Secondary != null)
                {
                    values.Add(Secondary.RemainingPercent);
                }

                if (IndividualLimit != null)
                {
                    values.Add(Math.Max(0, Math.Min(100, IndividualLimit.RemainingPercent)));
                }

                if (values.Count == 0)
                {
                    return null;
                }

                int minimum = values[0];
                for (int index = 1; index < values.Count; index++)
                {
                    minimum = Math.Min(minimum, values[index]);
                }

                return minimum;
            }
        }

        public bool IsUnlimited
        {
            get
            {
                return !DisplayRemainingPercent.HasValue && CreditsUnlimited;
            }
        }

        public bool IsOlderThan(TimeSpan age)
        {
            DateTime reference = ObservedAtUtc == DateTime.MinValue ? FetchedAtUtc : ObservedAtUtc;
            return reference != DateTime.MinValue && DateTime.UtcNow - reference > age;
        }

        public string BuildSummary()
        {
            StringBuilder text = new StringBuilder();
            if (IsUnlimited)
            {
                text.AppendLine("Codex 额度：无限");
            }
            else if (DisplayRemainingPercent.HasValue)
            {
                text.AppendLine("Codex 剩余额度：" + DisplayRemainingPercent.Value.ToString(CultureInfo.InvariantCulture) + "%");
            }
            else
            {
                text.AppendLine("Codex 剩余额度：暂不可用");
            }

            AppendWindowSummary(text, Primary);
            AppendWindowSummary(text, Secondary);

            if (IndividualLimit != null)
            {
                text.AppendLine("个人限额：剩余 " + IndividualLimit.RemainingPercent.ToString(CultureInfo.InvariantCulture) + "%");
            }

            if (!String.IsNullOrEmpty(CreditBalance))
            {
                text.AppendLine("Credits：" + CreditBalance);
            }

            if (ResetCreditCount.HasValue && ResetCreditCount.Value > 0)
            {
                text.AppendLine("可用重置次数：" + ResetCreditCount.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (AdditionalBuckets != null)
            {
                foreach (QuotaBucketInfo bucket in AdditionalBuckets)
                {
                    text.Append("其它额度 " + bucket.DisplayName + "：");
                    if (bucket.IsUnlimited)
                    {
                        text.AppendLine("无限");
                    }
                    else if (bucket.DisplayRemainingPercent.HasValue)
                    {
                        text.AppendLine("剩余 " + bucket.DisplayRemainingPercent.Value.ToString(CultureInfo.InvariantCulture) + "%");
                    }
                    else
                    {
                        text.AppendLine("百分比未知");
                    }
                }
            }

            if (!String.IsNullOrEmpty(PlanType))
            {
                text.AppendLine("套餐：" + QuotaFormatting.FormatPlan(PlanType));
            }

            text.Append("更新：" + FetchedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（" + SourceName + "）");
            return text.ToString();
        }

        private static void AppendWindowSummary(StringBuilder text, QuotaWindowInfo window)
        {
            if (window == null)
            {
                return;
            }

            text.Append(QuotaFormatting.FormatWindow(window.WindowMinutes));
            text.Append("窗口：剩余 ");
            text.Append(window.RemainingPercent.ToString(CultureInfo.InvariantCulture));
            text.Append("%");
            if (window.ResetLocalTime.HasValue)
            {
                text.Append("，");
                text.Append(QuotaFormatting.FormatReset(window.ResetLocalTime));
            }

            text.AppendLine();
        }
    }

    internal static class QuotaFormatting
    {
        public static string FormatWindow(long? minutes)
        {
            if (!minutes.HasValue || minutes.Value <= 0)
            {
                return "额度";
            }

            long value = minutes.Value;
            if (value % 1440 == 0)
            {
                return (value / 1440).ToString(CultureInfo.InvariantCulture) + " 天";
            }

            if (value % 60 == 0)
            {
                return (value / 60).ToString(CultureInfo.InvariantCulture) + " 小时";
            }

            return value.ToString(CultureInfo.InvariantCulture) + " 分钟";
        }

        public static string FormatReset(DateTime? localTime)
        {
            if (!localTime.HasValue)
            {
                return "重置时间未知";
            }

            DateTime value = localTime.Value;
            DateTime now = DateTime.Now;
            if (value.Date == now.Date)
            {
                return "今天 " + value.ToString("HH:mm") + " 重置";
            }

            if (value.Date == now.Date.AddDays(1))
            {
                return "明天 " + value.ToString("HH:mm") + " 重置";
            }

            return value.ToString("M月d日 HH:mm") + " 重置";
        }

        public static string FormatPlan(string plan)
        {
            if (String.IsNullOrEmpty(plan))
            {
                return "未知";
            }

            switch (plan.ToLowerInvariant())
            {
                case "free": return "Free";
                case "go": return "Go";
                case "plus": return "Plus";
                case "pro": return "Pro";
                case "prolite": return "Pro Lite";
                case "team": return "Team";
                case "business": return "Business";
                case "self_serve_business_usage_based": return "Business";
                case "enterprise": return "Enterprise";
                case "enterprise_cbp_usage_based": return "Enterprise";
                case "edu": return "Edu";
                default: return plan;
            }
        }
    }
}
