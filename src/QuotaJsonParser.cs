using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexQuotaTray
{
    internal static class QuotaJsonParser
    {
        public static QuotaSnapshot ParseAppServerResult(
            IDictionary<string, object> result,
            DateTime fetchedAtUtc)
        {
            if (result == null)
            {
                return null;
            }

            IDictionary<string, object> buckets = AsDictionary(GetValue(result, "rateLimitsByLimitId"));
            IDictionary<string, object> limits = AsDictionary(GetValue(result, "rateLimits"));
            string selectedBucketKey = null;
            if (limits == null && buckets != null)
            {
                limits = AsDictionary(GetValue(buckets, "codex"));
                if (limits != null)
                {
                    selectedBucketKey = "codex";
                }

                if (limits == null)
                {
                    foreach (KeyValuePair<string, object> pair in buckets)
                    {
                        limits = AsDictionary(pair.Value);
                        if (limits != null)
                        {
                            selectedBucketKey = pair.Key;
                            break;
                        }
                    }
                }
            }

            if (limits == null)
            {
                return null;
            }

            QuotaSnapshot snapshot = ParseSnapshot(limits, false);
            if (snapshot == null)
            {
                return null;
            }

            IDictionary<string, object> resetCredits = AsDictionary(GetValue(result, "rateLimitResetCredits"));
            if (resetCredits != null)
            {
                snapshot.ResetCreditCount = GetNullableInt(resetCredits, "availableCount");
            }

            snapshot.AdditionalBuckets = ParseAdditionalBuckets(buckets, snapshot, selectedBucketKey);

            snapshot.FetchedAtUtc = fetchedAtUtc;
            snapshot.ObservedAtUtc = fetchedAtUtc;
            snapshot.SourceName = "Codex 实时接口";
            snapshot.IsFallback = false;
            return snapshot;
        }

        private static List<QuotaBucketInfo> ParseAdditionalBuckets(
            IDictionary<string, object> buckets,
            QuotaSnapshot selected,
            string selectedBucketKey)
        {
            List<QuotaBucketInfo> additional = new List<QuotaBucketInfo>();
            if (buckets == null)
            {
                return additional;
            }

            foreach (KeyValuePair<string, object> pair in buckets)
            {
                QuotaSnapshot candidate = ParseSnapshot(AsDictionary(pair.Value), false);
                if (candidate == null)
                {
                    continue;
                }

                bool sameSelectedKey = !String.IsNullOrEmpty(selectedBucketKey) &&
                    String.Equals(selectedBucketKey, pair.Key, StringComparison.Ordinal);
                bool sameLimitId = !String.IsNullOrEmpty(selected.LimitId) &&
                    !String.IsNullOrEmpty(candidate.LimitId) &&
                    String.Equals(selected.LimitId, candidate.LimitId, StringComparison.Ordinal);
                if (sameSelectedKey || sameLimitId)
                {
                    continue;
                }

                if (String.IsNullOrEmpty(candidate.LimitId))
                {
                    candidate.LimitId = pair.Key;
                }

                additional.Add(QuotaBucketInfo.FromSnapshot(candidate));
            }

            return additional;
        }

        public static QuotaSnapshot ParseSessionEvent(
            IDictionary<string, object> root,
            DateTime fallbackTimestampUtc)
        {
            if (root == null || !String.Equals(GetString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                return null;
            }

            IDictionary<string, object> payload = AsDictionary(GetValue(root, "payload"));
            if (payload == null || !String.Equals(GetString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                return null;
            }

            IDictionary<string, object> limits = AsDictionary(GetValue(payload, "rate_limits"));
            if (limits == null)
            {
                return null;
            }

            QuotaSnapshot snapshot = ParseSnapshot(limits, true);
            if (snapshot == null)
            {
                return null;
            }

            DateTime eventTime = fallbackTimestampUtc;
            string timestamp = GetString(root, "timestamp");
            DateTime parsed;
            if (!String.IsNullOrEmpty(timestamp) &&
                DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                eventTime = parsed;
            }

            snapshot.ObservedAtUtc = eventTime;
            snapshot.FetchedAtUtc = DateTime.UtcNow;
            snapshot.SourceName = "Codex 本地缓存";
            snapshot.IsFallback = true;
            return snapshot;
        }

        private static QuotaSnapshot ParseSnapshot(IDictionary<string, object> limits, bool snakeCase)
        {
            string limitIdKey = snakeCase ? "limit_id" : "limitId";
            string limitNameKey = snakeCase ? "limit_name" : "limitName";
            string planTypeKey = snakeCase ? "plan_type" : "planType";
            string reachedKey = snakeCase ? "rate_limit_reached_type" : "rateLimitReachedType";
            string individualKey = snakeCase ? "individual_limit" : "individualLimit";

            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.LimitId = GetString(limits, limitIdKey);
            snapshot.LimitName = GetString(limits, limitNameKey);
            snapshot.PlanType = GetString(limits, planTypeKey);
            snapshot.RateLimitReachedType = GetString(limits, reachedKey);
            snapshot.Primary = ParseWindow(AsDictionary(GetValue(limits, "primary")), snakeCase);
            snapshot.Secondary = ParseWindow(AsDictionary(GetValue(limits, "secondary")), snakeCase);

            IDictionary<string, object> credits = AsDictionary(GetValue(limits, "credits"));
            if (credits != null)
            {
                snapshot.HasCredits = GetBool(credits, snakeCase ? "has_credits" : "hasCredits");
                snapshot.CreditsUnlimited = GetBool(credits, "unlimited");
                snapshot.CreditBalance = GetString(credits, "balance");
            }

            IDictionary<string, object> individual = AsDictionary(GetValue(limits, individualKey));
            if (individual != null)
            {
                int? remaining = GetNullableInt(individual, snakeCase ? "remaining_percent" : "remainingPercent");
                if (remaining.HasValue)
                {
                    snapshot.IndividualLimit = new SpendLimitInfo();
                    snapshot.IndividualLimit.RemainingPercent = Math.Max(0, Math.Min(100, remaining.Value));
                    snapshot.IndividualLimit.Limit = GetString(individual, "limit");
                    snapshot.IndividualLimit.Used = GetString(individual, "used");
                    snapshot.IndividualLimit.ResetsAtUnix = GetNullableLong(individual, snakeCase ? "resets_at" : "resetsAt");
                }
            }

            if (snapshot.Primary == null && snapshot.Secondary == null &&
                snapshot.IndividualLimit == null && !snapshot.CreditsUnlimited &&
                String.IsNullOrEmpty(snapshot.CreditBalance))
            {
                return null;
            }

            return snapshot;
        }

        private static QuotaWindowInfo ParseWindow(IDictionary<string, object> window, bool snakeCase)
        {
            if (window == null)
            {
                return null;
            }

            int? used = GetNullableInt(window, snakeCase ? "used_percent" : "usedPercent");
            if (!used.HasValue)
            {
                return null;
            }

            QuotaWindowInfo result = new QuotaWindowInfo();
            result.UsedPercent = Math.Max(0, Math.Min(100, used.Value));
            result.WindowMinutes = GetNullableLong(window, snakeCase ? "window_minutes" : "windowDurationMins");
            result.ResetsAtUnix = GetNullableLong(window, snakeCase ? "resets_at" : "resetsAt");
            return result;
        }

        public static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object>;
        }

        public static object GetValue(IDictionary<string, object> dictionary, string key)
        {
            if (dictionary == null)
            {
                return null;
            }

            object value;
            return dictionary.TryGetValue(key, out value) ? value : null;
        }

        public static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            if (value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        public static int? GetNullableInt(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        public static long? GetNullableLong(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
    }
}
