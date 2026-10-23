using System.Globalization;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record ImageRegion(string Id, string Text, double Left, double Top, double Right, double Bottom,
    int SourceWidth, int SourceHeight, string Coordinates = "oriented-normalized-v1", double? Confidence = null)
{
    public void Validate()
    {
        if (Id.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(Text) || Text.Length > 4096 ||
            SourceWidth < 1 || SourceHeight < 1 || Coordinates != "oriented-normalized-v1" ||
            new[] { Left, Top, Right, Bottom }.Any(value => !double.IsFinite(value) || value is < 0 or > 1) ||
            Right <= Left || Bottom <= Top || Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new InvalidDataException("Invalid image region geometry or evidence.");
    }
}

public static class RegionPostprocessor
{
    private static readonly Regex Box = new(@"(?<label>[^<>]+?)(?<boxes>(?:<loc_\d+>){4,})",
        RegexOptions.Compiled, TimeSpan.FromSeconds(3));
    private static readonly Regex Coordinate = new(@"<loc_(\d+)>", RegexOptions.Compiled, TimeSpan.FromSeconds(3));
    public static ImageTaskResult Parse(string task, string text, int width, int height, bool finished, int tokens)
    {
        if (width < 1 || height < 1 || text.Length > 1_048_576) throw new InvalidDataException("Invalid region input bounds.");
        string input = text;
        foreach (string envelope in new[] { "<od>", "</od>", "<box>", "</box>", "<bbox>", "</bbox>", "<ground>", "<obj>" })
            input = input.Replace(envelope, "", StringComparison.Ordinal);
        input = input.Trim();
        if (input.Length == 0 && finished) return new(task, "", "Empty", tokens, Regions: []);
        var regions = new List<ImageRegion>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        int consumed = 0;
        bool malformed = false, capped = false;
        foreach (Match match in Box.Matches(input))
        {
            if (!string.IsNullOrWhiteSpace(input[consumed..match.Index])) malformed = true;
            consumed = match.Index + match.Length;
            string label = FilePaths.CollapseWhitespace(match.Groups["label"].Value).Trim();
            var coordinates = Coordinate.Matches(match.Groups["boxes"].Value).ToArray();
            if (coordinates.Length % 4 != 0) malformed = true;
            for (int index = 0; index + 3 < coordinates.Length; index += 4)
            {
                if (!TryCoordinate(coordinates[index], out double left) || !TryCoordinate(coordinates[index + 1], out double top) ||
                    !TryCoordinate(coordinates[index + 2], out double right) || !TryCoordinate(coordinates[index + 3], out double bottom) ||
                    label.Length is < 1 or > 4096 || right <= left || bottom <= top)
                { malformed = true; continue; }
                string identity = FormattableString.Invariant($"{label}\n{left:R},{top:R},{right:R},{bottom:R}");
                if (!unique.Add(identity)) continue;
                if (regions.Count == 64) { capped = true; break; }
                regions.Add(new(regions.Count.ToString(CultureInfo.InvariantCulture), label, left, top, right, bottom, width, height));
            }
            if (capped) break;
        }
        if (!string.IsNullOrWhiteSpace(input[consumed..])) malformed = true;
        if (regions.Count == 0)
            return new(task, "", "Failed", tokens, Error: "The model returned no complete valid labeled regions.",
                ErrorCode: "invalid_region_output", Regions: []);
        string? warning = !finished ? "Region generation reached its output limit; only complete valid regions were retained." :
            capped ? "The region safety limit was reached; only the first 64 unique regions were retained." :
            malformed ? "Some region output was malformed; only complete valid regions were retained." : null;
        var result = new ImageTaskResult(task, string.Join("\n", regions.Select(region => region.Text)),
            warning is null ? "Completed" : "Partial", tokens, warning, Regions: regions.ToArray());
        result.Validate(task); return result;
    }
    private static bool TryCoordinate(Match match, out double value)
    {
        value = 0;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 0 or > 999)
            return false;
        value = (index + 0.5) / 1000;
        return true;
    }
}
