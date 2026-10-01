using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Pure tests for the labelling half of <see cref="NameRules"/>. Whether Revit accepts a name is
/// decided by Revit at runtime (live-verified); these pin down how a refusal is reported.
/// </summary>
public class NameRulesTests
{
    [Theory]
    // The exact naming convention from the consumer's report.
    [InlineData("[AutoAudit] doors.fire.ibc716", "[]")]
    [InlineData("a:b", ":")]
    [InlineData(@"a\b", @"\")]
    [InlineData("x{y}|z;", "{}|;")]
    [InlineData("<?~>", "<?~>")]
    public void Reports_every_illegal_character_once_in_order(string name, string expected)
        => Assert.Equal(expected.ToCharArray(), NameRules.IllegalCharsIn(name));

    [Theory]
    [InlineData("AutoAudit - doors.fire.ibc716")]   // the convention they switched to
    [InlineData("Door Schedule 3")]
    [InlineData("Lịch cửa — tầng 1 (2026)")]        // non-ASCII, dash, parentheses are fine
    [InlineData("")]
    public void Accepts_names_without_illegal_characters(string name)
        => Assert.Empty(NameRules.IllegalCharsIn(name));

    [Fact]
    public void Repeated_character_is_reported_once()
        => Assert.Equal(new[] { '[' }, NameRules.IllegalCharsIn("[[["));

    [Fact]
    public void Asterisk_is_allowed_in_a_view_name()
        // Measured live: Revit 2027 accepted "probe * x" as a schedule name. Labelling it illegal
        // would put a false rule in every error message.
        => Assert.DoesNotContain('*', NameRules.IllegalChars);

    [Fact]
    public void Backtick_is_refused_in_a_view_name()
        => Assert.Contains('`', NameRules.IllegalChars);

    [Fact]
    public void Covers_every_character_the_consumer_saw_rejected_live()
    {
        // Listed in the consumer's 2026-07-12 live probe (Revit 2027).
        foreach (var ch in @"[]{}|;<>?~:\")
            Assert.Contains(ch, NameRules.IllegalChars);
    }
}
