using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Core;

public class BeeXExpressionTests
{
    // ---- Basic Arithmetic Operations ----

    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("10-3", 7)]
    [InlineData("4*5", 20)]
    [InlineData("20/4", 5)]
    [InlineData("0+0", 0)]
    [InlineData("100", 100)]
    public void TryEvaluate_SimpleArithmetic_ReturnsTrue(string expr, double expected)
    {
        var ok = BeeXExpression.TryEvaluate(expr, out var value);
        ok.Should().BeTrue();
        value.Should().Be(expected);
    }

    // ---- Operator Precedence ----

    [Fact]
    public void TryEvaluate_MultiplicationBeforeAddition()
    {
        BeeXExpression.TryEvaluate("2+3*4", out var value).Should().BeTrue();
        value.Should().Be(14);
    }

    [Fact]
    public void TryEvaluate_DivisionBeforeSubtraction()
    {
        BeeXExpression.TryEvaluate("10-6/2", out var value).Should().BeTrue();
        value.Should().Be(7);
    }

    // ---- Parentheses and Operator Precedence ----

    [Fact]
    public void TryEvaluate_ParenthesesOverridePrecedence()
    {
        BeeXExpression.TryEvaluate("(2+3)*4", out var value).Should().BeTrue();
        value.Should().Be(20);
    }

    [Fact]
    public void TryEvaluate_NestedParentheses()
    {
        BeeXExpression.TryEvaluate("((1+2)*(3+4))", out var value).Should().BeTrue();
        value.Should().Be(21);
    }

    [Fact]
    public void TryEvaluate_DeeplyNestedParentheses()
    {
        BeeXExpression.TryEvaluate("(((2+3)))", out var value).Should().BeTrue();
        value.Should().Be(5);
    }

    // ---- Division by Zero ----

    [Fact]
    public void TryEvaluate_DivisionByZero_ReturnsFalse()
    {
        // double / 0 => Infinity, which is not finite => returns false
        BeeXExpression.TryEvaluate("1/0", out _).Should().BeFalse();
    }

    // ---- Invalid Expression ----

    [Theory]
    [InlineData("abc")]
    [InlineData("1 2 +")]
    [InlineData("*5")]
    [InlineData("()")]
    [InlineData("(1+2")]
    [InlineData("1+2)")]
    public void TryEvaluate_InvalidExpression_ReturnsFalse(string expr)
    {
        BeeXExpression.TryEvaluate(expr, out _).Should().BeFalse();
    }

    // ---- Empty string ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryEvaluate_EmptyOrWhitespace_ReturnsFalse(string expr)
    {
        BeeXExpression.TryEvaluate(expr, out _).Should().BeFalse();
    }

    // ---- Unicode Operators (× ÷) ----

    [Fact]
    public void TryEvaluate_UnicodeMultiply()
    {
        BeeXExpression.TryEvaluate("3×4", out var value).Should().BeTrue();
        value.Should().Be(12);
    }

    [Fact]
    public void TryEvaluate_UnicodeDivide()
    {
        BeeXExpression.TryEvaluate("12÷3", out var value).Should().BeTrue();
        value.Should().Be(4);
    }

    // ---- Big Number Processing ----

    [Fact]
    public void TryEvaluate_LargeNumbers()
    {
        BeeXExpression.TryEvaluate("999999*999999", out var value).Should().BeTrue();
        value.Should().Be(999998000001);
    }

    // ---- Handling Blank Spaces ----

    [Fact]
    public void TryEvaluate_WhitespaceAroundExpression()
    {
        BeeXExpression.TryEvaluate("  1 + 2 * 3  ", out var value).Should().BeTrue();
        value.Should().Be(7);
    }

    [Fact]
    public void TryEvaluate_WhitespaceInsideParentheses()
    {
        BeeXExpression.TryEvaluate("( 1 + 2 ) * 3", out var value).Should().BeTrue();
        value.Should().Be(9);
    }

    // ---- Decimals and Commas vs. Decimal Points ----

    [Fact]
    public void TryEvaluate_DecimalPoint()
    {
        BeeXExpression.TryEvaluate("1.5+2.5", out var value).Should().BeTrue();
        value.Should().Be(4);
    }

    [Fact]
    public void TryEvaluate_CommaAsDecimalSeparator()
    {
        // Parser treats ',' as decimal separator (replaces with '.')
        BeeXExpression.TryEvaluate("1,5+2,5", out var value).Should().BeTrue();
        value.Should().Be(4);
    }

    // ---- Plus and Minus Signs ----

    [Fact]
    public void TryEvaluate_UnaryMinus()
    {
        BeeXExpression.TryEvaluate("-5+3", out var value).Should().BeTrue();
        value.Should().Be(-2);
    }

    [Fact]
    public void TryEvaluate_UnaryPlus()
    {
        BeeXExpression.TryEvaluate("+5*2", out var value).Should().BeTrue();
        value.Should().Be(10);
    }

    [Fact]
    public void TryEvaluate_DoublePlus_IsUnaryPlus()
    {
        // "1++2" is parsed as 1 + (+2) = 3
        BeeXExpression.TryEvaluate("1++2", out var value).Should().BeTrue();
        value.Should().Be(3);
    }

    // ---- Floating-Point Precision ----

    [Fact]
    public void TryEvaluate_FractionalDivision()
    {
        BeeXExpression.TryEvaluate("7/2", out var value).Should().BeTrue();
        value.Should().Be(3.5);
    }

    // ---- Implicit Multiplication (Number/Parentheses Immediately Following the Left Parenthesis) ----

    [Theory]
    [InlineData("2(9*8)", 144)]        // 2 * 72
    [InlineData("1+1/2(9*8)", 37)]     // 1 + (1/2) * 72; left-to-right evaluation with equal precedence
    [InlineData("(1+2)(3+4)", 21)]     // 3 * 7
    [InlineData("3(4)", 12)]
    public void TryEvaluate_ImplicitMultiplicationBeforeParen(string expr, double expected)
    {
        BeeXExpression.TryEvaluate(expr, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    // ---- Full-width characters (numbers, operators, parentheses) are equivalent to half-width characters ----

    [Theory]
    [InlineData("１＋２", 3)]
    [InlineData("２×３", 6)]
    [InlineData("１０／４", 2.5)]
    [InlineData("１＋１／２（９＊８）", 37)]   // Full-Width Version User Case Studies
    [InlineData("（１＋２）＊３", 9)]
    [InlineData("１０－２", 8)]              // Full-width hyphen －
    public void TryEvaluate_FullWidthSymbols_EquivalentToHalfWidth(string expr, double expected)
    {
        BeeXExpression.TryEvaluate(expr, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void TryEvaluate_UnicodeMinusSign_Normalized()
    {
        // U+2212 Mathematical Minus Sign −
        BeeXExpression.TryEvaluate("5\u22123", out var value).Should().BeTrue();
        value.Should().Be(2);
    }
}
