namespace SocialGraph.Api.Tests;

using SocialGraph.Api.Service;

public sealed class InputSecurityTests
{
    [Fact]
    public void Display_name_normalizes_compatibility_forms_and_collapses_spacing()
    {
        var normalized = InputSecurity.NormalizeText(
            "  Ｆａｋｅｂｏｏｋ\u00a0User  ",
            "name",
            InputSecurity.MaxDisplayNameLength,
            multiline: false,
            collapseWhitespace: true,
            maxCombiningMarks: 16);

        Assert.Equal("Fakebook User", normalized);
    }

    [Fact]
    public void Display_name_rejects_zalgo_and_bidi_controls()
    {
        var zalgo = "A" + new string('\u0301', 17);
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeText(
            zalgo,
            "name",
            InputSecurity.MaxDisplayNameLength,
            multiline: false,
            collapseWhitespace: true,
            maxCombiningMarks: 16));

        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeText(
            "alice\u202E.com",
            "name",
            InputSecurity.MaxDisplayNameLength,
            multiline: false,
            collapseWhitespace: true,
            maxCombiningMarks: 16));
    }

    [Fact]
    public void Text_limits_are_enforced_by_unicode_character_count()
    {
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeText(
            new string('x', InputSecurity.MaxPostLength + 1),
            "content",
            InputSecurity.MaxPostLength,
            multiline: true));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeText(
            new string('x', InputSecurity.MaxCommentLength + 1),
            "content",
            InputSecurity.MaxCommentLength,
            multiline: true));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeText(
            new string('x', InputSecurity.MaxStoryLength + 1),
            "content",
            InputSecurity.MaxStoryLength,
            multiline: true));
    }

    [Fact]
    public void Story_background_metadata_does_not_reduce_the_visible_text_limit()
    {
        var text = new string('x', InputSecurity.MaxStoryLength);
        var encoded = $"[[story-bg:#7c3aed]]\n{text}";

        Assert.Equal(encoded, InputSecurity.NormalizeStoryContent(encoded));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeStoryContent(
            $"[[story-bg:#7c3aed]]\n{text}x"));
    }

    [Fact]
    public void Story_metadata_allowance_is_not_granted_to_unapproved_colors()
    {
        var untrustedEnvelope = "[[story-bg:#ffffff]]\n" + new string('x', 105);

        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeStoryContent(untrustedEnvelope));
    }

    [Theory]
    [InlineData("2000-02-29")]
    [InlineData("1999-12-31")]
    public void Birthdate_accepts_only_real_iso_dates(string value)
    {
        Assert.Equal(DateOnly.ParseExact(value, "yyyy-MM-dd"), InputSecurity.NormalizeBirthdate(value));
    }

    [Theory]
    [InlineData("2001-02-29")]
    [InlineData("2000-01-01T00:00:00Z")]
    [InlineData("not-a-date")]
    [InlineData("1899-12-31")]
    public void Birthdate_rejects_invalid_or_ambiguous_values(string value)
    {
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeBirthdate(value));
    }

    [Fact]
    public void Birthdate_rejects_out_of_range_typed_dates()
    {
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeBirthdate(new DateOnly(1899, 12, 31)));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeBirthdate(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)));
    }

    [Fact]
    public void Email_is_normalized_and_bounded()
    {
        Assert.Equal("person@example.com", InputSecurity.NormalizeEmail("Person@Example.COM"));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeEmail("person@@example.com"));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeEmail("\"person\"@example.com"));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeEmail("person@[127.0.0.1]"));
        Assert.Throws<ArgumentException>(() => InputSecurity.NormalizeEmail(new string('a', 250) + "@x.test"));
    }

    [Fact]
    public void Email_accepts_the_standard_254_character_and_64_character_local_part_boundary()
    {
        var local = new string('a', 64);
        var domain = new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
        var email = $"{local}@{domain}";

        Assert.Equal(InputSecurity.MaxEmailLength, email.Length);
        Assert.Equal(email, InputSecurity.NormalizeEmail(email));
    }
}
