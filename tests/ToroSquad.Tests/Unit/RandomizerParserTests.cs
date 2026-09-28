using ToroSquad.Modules.Randomizer.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>TSQ Randomizer input rules: the /zarat notation, the /randomsayi range and the /sec option list.</summary>
public sealed class RandomizerParserTests
{
    // ---- /zarat ----

    [Theory]
    [InlineData("1-20", 1, 20)]
    [InlineData("2-6", 2, 6)]
    [InlineData("3-100", 3, 100)]
    [InlineData("1d20", 1, 20)]
    [InlineData("2d6", 2, 6)]
    [InlineData("3d100", 3, 100)]
    [InlineData("2D6", 2, 6)]
    [InlineData("  2d6  ", 2, 6)]
    [InlineData("\t1-20 ", 1, 20)]
    [InlineData("1d2", 1, 2)]           // minimum count and minimum sides
    [InlineData("20d10000", 20, 10_000)] // maximum count and maximum sides
    [InlineData("20-10000", 20, 10_000)]
    [InlineData("02d06", 2, 6)]         // leading zeros are unambiguous
    public void Dice_accepts_both_notations(string input, int count, int sides)
    {
        var result = DiceNotation.Parse(input);
        result.ErrorKey.Should().BeNull();
        result.Spec.Should().Be(new DiceSpec(count, sides));
    }

    [Fact]
    public void Dice_notation_is_normalized_for_the_card()
    {
        DiceNotation.Parse("2-6").Spec!.Notation.Should().Be("2d6");
        DiceNotation.Parse("2D6").Spec!.Notation.Should().Be("2d6");
        DiceNotation.Parse("1d20").Spec!.Notation.Should().Be("1d20");
    }

    [Theory]
    [InlineData("0-6", DiceNotation.TooFewKey)]
    [InlineData("0d6", DiceNotation.TooFewKey)]
    [InlineData("21-6", DiceNotation.TooManyKey)]
    [InlineData("21d6", DiceNotation.TooManyKey)]
    [InlineData("2-1", DiceNotation.TooFewSidesKey)]
    [InlineData("1-0", DiceNotation.TooFewSidesKey)]
    [InlineData("1d10001", DiceNotation.TooManySidesKey)]
    [InlineData("1-2147483647", DiceNotation.TooManySidesKey)]            // int.MaxValue: sides + 1 would overflow
    [InlineData("1-99999999999999999999999", DiceNotation.TooManySidesKey)] // longer than any integer type
    [InlineData("99999999999999999999-6", DiceNotation.TooManyKey)]
    [InlineData("4294967297d6", DiceNotation.TooManyKey)]                 // would wrap to 1 as a 32-bit value
    public void Dice_out_of_range_values_are_refused_by_range(string input, string expected)
    {
        var result = DiceNotation.Parse(input);
        result.Spec.Should().BeNull();
        result.ErrorKey.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2x6")]
    [InlineData("2*6")]
    [InlineData("d6")]
    [InlineData("2d")]
    [InlineData("2-")]
    [InlineData("-6")]
    [InlineData("20")]
    [InlineData("2 d 6")]
    [InlineData("2 - 6")]
    [InlineData("2dd6")]
    [InlineData("2--6")]
    [InlineData("2d6d6")]
    [InlineData("2-6-8")]
    [InlineData("2d6+1")]
    [InlineData("2d6 abc")]
    [InlineData("roll 2d6")]
    [InlineData("x2d6")]
    [InlineData("2d6!")]
    [InlineData("+2d6")]
    [InlineData("-2d6")]
    [InlineData("2d-6")]
    [InlineData("2.5d6")]
    [InlineData("2,d6")]
    [InlineData("2d6\n2d6")]
    [InlineData("٢d٦")]  // Arabic-Indic digits
    [InlineData("２d６")] // full-width digits
    [InlineData("2ｄ6")] // full-width d
    public void Dice_malformed_input_is_invalid(string? input)
    {
        var result = DiceNotation.Parse(input);
        result.Spec.Should().BeNull();
        result.ErrorKey.Should().Be(DiceNotation.InvalidKey);
    }

    [Fact]
    public void Dice_input_longer_than_the_option_limit_is_invalid()
    {
        DiceNotation.Parse(new string('0', DiceNotation.MaxInputLength) + "2d6").ErrorKey.Should().Be(DiceNotation.InvalidKey);
        DiceNotation.Parse(new string('0', DiceNotation.MaxInputLength - 3) + "2d6").Spec.Should().Be(new DiceSpec(2, 6));
    }

    // ---- /randomsayi ----

    [Fact]
    public void Number_minimum_defaults_to_one()
    {
        NumberRanges.Create(100, null).Range.Should().Be(new NumberRange(1, 100));
        NumberRanges.DefaultMinimum.Should().Be(1);
    }

    [Theory]
    [InlineData(100, 50, 50, 100)]
    [InlineData(10, 10, 10, 10)]      // min == max
    [InlineData(100, -100, -100, 100)] // negative range
    [InlineData(-5, -10, -10, -5)]    // entirely negative
    [InlineData(0, 0, 0, 0)]
    [InlineData(1_000_000_000, -1_000_000_000, -1_000_000_000, 1_000_000_000)] // the supported limits
    public void Number_range_accepts_inclusive_bounds(long maximum, long minimum, int expectedMin, int expectedMax)
    {
        var result = NumberRanges.Create(maximum, minimum);
        result.ErrorKey.Should().BeNull();
        result.Range.Should().Be(new NumberRange(expectedMin, expectedMax));
    }

    [Theory]
    [InlineData(10, 11)]
    [InlineData(-5, null)] // default minimum 1 is above a negative maximum
    [InlineData(0, null)]
    public void Number_minimum_above_maximum_is_refused(int maximum, int? minimum) =>
        NumberRanges.Create(maximum, minimum).ErrorKey.Should().Be(NumberRanges.MinAboveMaxKey);

    [Theory]
    [InlineData(1_000_000_001, 1)]
    [InlineData(100, -1_000_000_001)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(100, long.MinValue)]
    [InlineData(9_007_199_254_740_991, 1)] // Discord's largest integer option value
    public void Number_values_outside_the_supported_span_are_refused(long maximum, long minimum)
    {
        var result = NumberRanges.Create(maximum, minimum);
        result.Range.Should().BeNull();
        result.ErrorKey.Should().Be(NumberRanges.OutOfRangeKey);
    }

    [Fact]
    public void Number_limit_leaves_room_for_the_exclusive_upper_bound() =>
        ((long)NumberRanges.Limit + 1).Should().BeLessThanOrEqualTo(int.MaxValue);

    // ---- /sec ----

    [Fact]
    public void Choice_splits_on_commas_and_trims()
    {
        ChoiceList.Parse("CS2, Valheim ,WoW").Options.Should().Equal("CS2", "Valheim", "WoW");
        ChoiceList.Parse("CS2,Valheim").Options.Should().Equal("CS2", "Valheim");
    }

    [Theory]
    [InlineData("A,B,C")]
    [InlineData("A|B|C")]
    [InlineData("A, B | C")]
    [InlineData("A|||B,,C")]
    [InlineData(" A |, B ,| C ")]
    public void Choice_commas_and_pipes_both_separate_even_mixed(string input) =>
        ChoiceList.Parse(input).Options.Should().Equal("A", "B", "C");

    [Fact]
    public void Choice_mixed_separators_give_the_same_options_as_either_one()
    {
        var expected = new[] { "CS2", "Valheim", "WoW" };
        ChoiceList.Parse("CS2, Valheim, WoW").Options.Should().Equal(expected);
        ChoiceList.Parse("CS2 | Valheim | WoW").Options.Should().Equal(expected);
        ChoiceList.Parse("CS2, Valheim | WoW").Options.Should().Equal(expected);
        ChoiceList.Parse("Pizza, kola | Burger").Options.Should().Equal("Pizza", "kola", "Burger");
    }

    [Fact]
    public void Choice_mixed_separator_duplicates_are_cleaned_before_the_count_check()
    {
        var result = ChoiceList.Parse("A, a | A");
        result.Options.Should().BeNull("one distinct option is left");
        result.ErrorKey.Should().Be(ChoiceList.TooFewKey);
        ChoiceList.Parse("cs2 | CS2, Valheim | VALHEIM").Options.Should().Equal("cs2", "Valheim");
    }

    [Fact]
    public void Choice_drops_empty_entries()
    {
        ChoiceList.Parse("CS2,,Valheim").Options.Should().Equal("CS2", "Valheim");
        ChoiceList.Parse(" , CS2 , , Valheim , ").Options.Should().Equal("CS2", "Valheim");
        ChoiceList.Parse("||CS2||Valheim||").Options.Should().Equal("CS2", "Valheim");
    }

    [Fact]
    public void Choice_duplicates_count_once_case_insensitively_keeping_the_first_spelling()
    {
        ChoiceList.Parse("CS2, CS2, Valheim").Options.Should().Equal("CS2", "Valheim");
        ChoiceList.Parse("CS2, cs2, Valheim, VALHEIM, WoW").Options.Should().Equal("CS2", "Valheim", "WoW");
        ChoiceList.Parse("cs2, CS2, x").Options.Should().Equal("cs2", "x");
        ChoiceList.Parse("Among  Us, among us, x").Options.Should().Equal(["Among Us", "x"], "inner whitespace collapses first");
    }

    [Theory]
    [InlineData("CS2, CS2")]
    [InlineData("CS2, cs2, Cs2")]
    [InlineData("CS2")]
    [InlineData("CS2,")]
    [InlineData(",,,")]
    [InlineData(" | | ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Choice_needs_two_different_options(string? input)
    {
        var result = ChoiceList.Parse(input);
        result.Options.Should().BeNull();
        result.ErrorKey.Should().Be(ChoiceList.TooFewKey);
    }

    [Fact]
    public void Choice_limits_the_number_of_options()
    {
        var twentyFive = string.Join(",", Enumerable.Range(1, 25).Select(i => "o" + i));
        ChoiceList.Parse(twentyFive).Options.Should().HaveCount(25);
        ChoiceList.Parse(twentyFive + ",o26").ErrorKey.Should().Be(ChoiceList.TooManyKey);
        ChoiceList.Parse(twentyFive + ",O25,o1").Options.Should().HaveCount(25, "duplicates do not count towards the limit");
    }

    [Fact]
    public void Choice_limits_option_and_input_length()
    {
        var hundred = new string('a', ChoiceList.MaxOptionLength);
        ChoiceList.Parse(hundred + ", b").Options.Should().Equal(hundred, "b");
        ChoiceList.Parse(hundred + "a, b").ErrorKey.Should().Be(ChoiceList.TooLongKey);
        ChoiceList.Parse("   " + hundred + "   , b").Options.Should().Equal(hundred, "b");

        var longInput = "a, b," + new string(' ', ChoiceList.MaxInputLength);
        ChoiceList.Parse(longInput).ErrorKey.Should().Be(ChoiceList.InputTooLongKey);
    }

    [Fact]
    public void Choice_options_never_span_lines()
    {
        var options = ChoiceList.Parse("Line\none\r\n\r\ntwo, \n\n\n Valheim \t WoW").Options!;
        options.Should().Equal("Line one two", "Valheim WoW");
        options.Should().NotContain(o => o.Contains('\n') || o.Contains('\r') || o.Contains('\t'));
        ChoiceList.Parse("\n, \n, a").ErrorKey.Should().Be(ChoiceList.TooFewKey);
        ChoiceList.Parse("Line\none |\r\n two ,\t\tthree\n").Options.Should().Equal("Line one", "two", "three");
        ChoiceList.Parse("\n|\n, a").ErrorKey.Should().Be(ChoiceList.TooFewKey);
    }

    [Fact]
    public void Choice_keeps_the_typed_text_until_it_is_shown()
    {
        // Parsing does not escape; RandomizerCards defuses when rendering (tested in RandomizerCardTests).
        ChoiceList.Parse("@everyone, <@&123>").Options.Should().Equal("@everyone", "<@&123>");
    }
}
