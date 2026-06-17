using System;

namespace Odin_Flash.Util
{
    /// <summary>URLs SamFW a partir de datos DVIF (model, sales/CSC, build/PDA).</summary>
    public static class SamFwLinkBuilder
    {
        const string BaseUrl = "https://samfw.com/firmware";

        /// <summary>
        /// Enlace directo al firmware leído: /firmware/{model}/{sales}/{build}.
        /// </summary>
        public static string BuildFirmwareUrl(string model, string salesCode, string buildNumber)
        {
            model = NormalizeSegment(model);
            salesCode = NormalizeSegment(salesCode);
            buildNumber = NormalizeSegment(buildNumber);

            if (string.IsNullOrEmpty(model))
                return null;

            if (!string.IsNullOrEmpty(salesCode) && !string.IsNullOrEmpty(buildNumber))
                return $"{BaseUrl}/{model}/{salesCode}/{buildNumber}";

            if (!string.IsNullOrEmpty(salesCode))
                return $"{BaseUrl}/{model}/{salesCode}";

            if (!string.IsNullOrEmpty(buildNumber))
                return $"{BaseUrl}/{model}/{buildNumber}";

            return $"{BaseUrl}/{model}";
        }

        static string NormalizeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim().ToUpperInvariant();
            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
                return null;

            return value;
        }
    }
}
