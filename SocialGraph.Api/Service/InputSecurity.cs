namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text;
using SocialGraph.Api.Contracts;

/// <summary>
/// Central input boundary for values that can be written to the SocialGraph object store.
/// Keeping this policy below the GraphQL resolver is intentional: outbox/replay and unit-test
/// callers must receive the same limits as browser traffic.
/// </summary>
internal static class InputSecurity
{
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 80;
    public const int MaxProfileDescriptionLength = 255;
    public const int MaxLocationLength = 255;
    public const int MaxGroupNameLength = 120;
    public const int MaxGroupDescriptionLength = 2_000;
    public const int MaxPostLength = 63_206;
    public const int MaxCommentLength = 8_000;
    public const int MaxStoryLength = 125;
    public const int MaxUrlLength = 2_048;
    public const int MaxMediaPerContent = 10;
    public const int MaxReferencedUsers = 50;
    public const int MaxPasswordLength = 128;
    public const int MaxCombiningMarks = 256;
    private const int StoryBackgroundMetadataLength = 20;
    private const int StoryBackgroundEnvelopeOverhead = StoryBackgroundMetadataLength + 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedStoryBackgroundMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "[[story-bg:#0866ff]]",
        "[[story-bg:#7c3aed]]",
        "[[story-bg:#d63384]]",
        "[[story-bg:#e67e22]]",
        "[[story-bg:#11998e]]",
        "[[story-bg:#242526]]"
    };

    public static string RequiredText(
        string? value,
        string field,
        int maxLength,
        bool multiline = true,
        bool collapseWhitespace = false,
        int? maxCombiningMarks = null)
    {
        if (value is null)
        {
            throw new ArgumentException($"{field} is required.", field);
        }

        var normalized = NormalizeText(
            value,
            field,
            maxLength,
            multiline,
            collapseWhitespace,
            maxCombiningMarks);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{field} must not be empty.", field);
        }

        return normalized;
    }

    public static string OptionalText(
        string? value,
        string field,
        int maxLength,
        bool multiline = true,
        bool collapseWhitespace = false,
        int? maxCombiningMarks = null,
        bool allowEmpty = true)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var normalized = NormalizeText(
            value,
            field,
            maxLength,
            multiline,
            collapseWhitespace,
            maxCombiningMarks);
        if (!allowEmpty && normalized.Length == 0)
        {
            throw new ArgumentException($"{field} must not be empty.", field);
        }

        return normalized;
    }

    public static string NormalizeText(
        string value,
        string field,
        int maxLength,
        bool multiline,
        bool collapseWhitespace = false,
        int? maxCombiningMarks = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        // A decomposed representation can be larger than its composed form. A small
        // multiplier preserves legitimate scripts while preventing an attacker from
        // forcing an unbounded normalization/GC pass before the real limit is checked.
        if (value.Length > checked(maxLength * 2 + 32))
        {
            throw TooLong(field, maxLength);
        }

        ValidateUtf16(value, field);
        var normalizedInput = value.Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var builder = new StringBuilder(normalizedInput.Length);
        var runeCount = 0;
        var combiningCount = 0;
        var consecutiveCombining = 0;
        var effectiveCombiningLimit = maxCombiningMarks ?? Math.Min(MaxCombiningMarks, Math.Max(16, maxLength / 8));
        var previousWhitespace = false;

        foreach (var rune in normalizedInput.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            // Newline and tab are the only Cc characters accepted, and only for
            // fields explicitly marked multiline. Handle them before the broad
            // control-character rejection below so ordinary post/comment line
            // breaks remain usable.
            if (rune.Value is '\n' or '\t')
            {
                if (!multiline)
                {
                    throw new ArgumentException($"{field} must be a single line.", field);
                }

                if (collapseWhitespace)
                {
                    if (!previousWhitespace)
                    {
                        AppendChar(' ');
                        previousWhitespace = true;
                    }
                }
                else
                {
                    AppendRune(rune);
                }

                continue;
            }

            if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                if (!multiline)
                {
                    throw new ArgumentException($"{field} must be a single line.", field);
                }

                AppendChar('\n');
                previousWhitespace = false;
                continue;
            }

            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.Surrogate or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned)
            {
                throw new ArgumentException($"{field} contains an unsupported control or formatting character.", field);
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (collapseWhitespace)
                {
                    if (!previousWhitespace)
                    {
                        AppendChar(' ');
                        previousWhitespace = true;
                    }
                }
                else
                {
                    AppendRune(rune);
                }

                continue;
            }

            previousWhitespace = false;
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                combiningCount++;
                consecutiveCombining++;
                if (consecutiveCombining > 3 || combiningCount > effectiveCombiningLimit)
                {
                    throw new ArgumentException($"{field} contains excessive combining marks.", field);
                }
            }
            else
            {
                consecutiveCombining = 0;
            }

            AppendRune(rune);
        }

        var result = builder.ToString();
        if (collapseWhitespace)
        {
            result = result.Trim();
        }

        if (result.EnumerateRunes().Count() > maxLength)
        {
            throw TooLong(field, maxLength);
        }

        return result;

        void AppendRune(Rune rune)
        {
            runeCount++;
            if (runeCount > maxLength)
            {
                throw TooLong(field, maxLength);
            }

            builder.Append(rune.ToString());
        }

        void AppendChar(char character)
        {
            AppendRune(new Rune(character));
        }
    }

    /// <summary>
    /// Normalizes a story while treating the trusted text-background envelope as
    /// metadata. The public 125-character limit applies to text a viewer renders,
    /// not to the fixed metadata prefix stored alongside it.
    /// </summary>
    public static string NormalizeStoryContent(string? value, string field = "content")
    {
        var envelope = OptionalText(
            value,
            field,
            MaxStoryLength + StoryBackgroundEnvelopeOverhead,
            multiline: true,
            maxCombiningMarks: MaxCombiningMarks);

        if (envelope.Length < StoryBackgroundMetadataLength)
        {
            return OptionalText(
                envelope,
                field,
                MaxStoryLength,
                multiline: true,
                maxCombiningMarks: MaxCombiningMarks);
        }

        var metadata = envelope[..StoryBackgroundMetadataLength];
        if (!AllowedStoryBackgroundMetadata.Contains(metadata))
        {
            return OptionalText(
                envelope,
                field,
                MaxStoryLength,
                multiline: true,
                maxCombiningMarks: MaxCombiningMarks);
        }

        var contentStart = envelope.Length > StoryBackgroundMetadataLength &&
                           envelope[StoryBackgroundMetadataLength] == '\n'
            ? StoryBackgroundEnvelopeOverhead
            : StoryBackgroundMetadataLength;
        var visibleText = OptionalText(
            envelope[contentStart..],
            field,
            MaxStoryLength,
            multiline: true,
            maxCombiningMarks: MaxCombiningMarks);
        var canonicalMetadata = metadata.ToLowerInvariant();
        return visibleText.Length == 0
            ? canonicalMetadata
            : $"{canonicalMetadata}\n{visibleText}";
    }

    public static string NormalizeEmail(string? value, string field = "email")
    {
        var email = RequiredText(value, field, MaxEmailLength, multiline: false, collapseWhitespace: true, maxCombiningMarks: 8);
        if (email.Length > MaxEmailLength || email.Any(char.IsWhiteSpace) || email.Count(c => c == '@') != 1)
        {
            throw new ArgumentException("Email is not valid.", field);
        }

        if (email.Any(character => character > '\x7f' || char.IsControl(character)))
        {
            throw new ArgumentException("Email is not valid.", field);
        }

        var at = email.IndexOf('@');
        if (at is <= 0 or > 64 || at == email.Length - 1 ||
            at != email.LastIndexOf('@') ||
            email[..at][0] == '.' ||
            email[..at][^1] == '.' ||
            email[..at].Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Email is not valid.", field);
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        if (local.Any(character => !IsAllowedEmailLocalCharacter(character)) ||
            domain.Length > 253 || domain[0] == '.' || domain[^1] == '.')
        {
            throw new ArgumentException("Email is not valid.", field);
        }

        foreach (var label in domain.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                throw new ArgumentException("Email is not valid.", field);
            }
        }

        return email.ToLowerInvariant();
    }

    public static string ValidatePassword(string? value, string field = "password")
    {
        // Authentication owns password-strength policy. SocialGraph only needs a bounded,
        // transport-safe opaque value because the encrypted outbox forwards it there.
        if (string.IsNullOrEmpty(value) || value.Length > MaxPasswordLength)
        {
            throw new ArgumentException($"{field} must contain at most {MaxPasswordLength} characters.", field);
        }

        // Passwords are secrets, so never normalize or trim them. We still reject invalid
        // UTF-16/control values that could poison JSON/log/transport boundaries.
        ValidateUtf16(value, field);
        try
        {
            if (StrictUtf8.GetByteCount(value) > 72)
            {
                throw new ArgumentException($"{field} is too long for the credential provider.", field);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException($"{field} contains an invalid Unicode sequence.", field);
        }
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned)
            {
                throw new ArgumentException($"{field} contains an unsupported character.", field);
            }
        }

        return value;
    }

    public static DateOnly NormalizeBirthdate(DateOnly value, string field = "birthdate")
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (value < new DateOnly(1900, 1, 1) || value > today)
        {
            throw new ArgumentException("Birthdate is outside the supported range.", field);
        }

        return value;
    }

    // Kept for non-GraphQL replay/import boundaries that still carry the legacy
    // JSON representation. Browser GraphQL input is DateOnly and is rejected by
    // the Date scalar before a resolver is invoked.
    public static DateOnly NormalizeBirthdate(string? value, string field = "birthdate")
    {
        var raw = RequiredText(value, field, 10, multiline: false, collapseWhitespace: true, maxCombiningMarks: 0);
        if (!DateOnly.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new ArgumentException("Birthdate must be a valid date in yyyy-MM-dd format.", field);
        }

        return NormalizeBirthdate(date, field);
    }

    public static string NormalizeUrl(string? value, string field = "url", bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(value) && allowEmpty)
        {
            return string.Empty;
        }

        var url = RequiredText(value, field, MaxUrlLength, multiline: false, collapseWhitespace: true, maxCombiningMarks: 0);
        if (url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\') ||
            url.Any(char.IsControl) || url.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var parsed) ||
            (parsed.IsAbsoluteUri && parsed.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("URL is not valid.", field);
        }

        return url;
    }

    public static IReadOnlyList<MediaInput> NormalizeMedia(
        IReadOnlyList<MediaInput>? media,
        string field = "media")
    {
        if (media is null)
        {
            return Array.Empty<MediaInput>();
        }

        if (media.Count > MaxMediaPerContent)
        {
            throw new ArgumentException($"{field} must contain at most {MaxMediaPerContent} items.", field);
        }

        var result = new List<MediaInput>(media.Count);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media)
        {
            if (item is null || item.Type is < 0 or > 2)
            {
                throw new ArgumentException($"{field} contains an unsupported media type.", field);
            }

            var url = NormalizeUrl(item.Url, $"{field}.url");
            if (!seenUrls.Add(url))
            {
                throw new ArgumentException($"{field} contains duplicate media.", field);
            }

            result.Add(item with { Url = url });
        }

        return result;
    }

    public static IReadOnlyList<long> NormalizeIds(
        IReadOnlyList<long>? ids,
        string field,
        int maximum = MaxReferencedUsers)
    {
        if (ids is null)
        {
            return Array.Empty<long>();
        }

        if (ids.Count > maximum)
        {
            throw new ArgumentException($"{field} must contain at most {maximum} items.", field);
        }

        var result = new List<long>(ids.Count);
        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            if (id <= 0 || !seen.Add(id))
            {
                throw new ArgumentException($"{field} contains an invalid or duplicate ID.", field);
            }

            result.Add(id);
        }

        return result;
    }

    public static void ValidatePositiveId(long id, string field)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(field, "ID must be a positive signed 64-bit integer.");
        }
    }

    private static ArgumentException TooLong(string field, int maxLength) =>
        new($"{field} must not exceed {maxLength} Unicode characters.", field);

    private static bool IsAllowedEmailLocalCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) ||
        character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or
            '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~' or '.';

    private static void ValidateUtf16(string value, string field)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
            {
                throw new ArgumentException($"{field} contains an invalid Unicode sequence.", field);
            }

            index++;
        }
    }
}
