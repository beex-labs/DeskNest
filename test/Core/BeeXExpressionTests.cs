using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Core;

public class BeeXExpressionTests
{
    // ---- 简单四则运算 ----

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

    // ---- 运算符优先级 ----

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

    // ---- 括号覆盖优先级 ----

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

    // ---- 除零 ----

    [Fact]
    public void TryEvaluate_DivisionByZero_ReturnsFalse()
    {
        // double / 0 => Infinity, which is not finite => returns false
        BeeXExpression.TryEvaluate("1/0", out _).Should().BeFalse();
    }

    // ---- 无效表达式 ----

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

    // ---- 空字符串 ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryEvaluate_EmptyOrWhitespace_ReturnsFalse(string expr)
    {
        BeeXExpression.TryEvaluate(expr, out _).Should().BeFalse();
    }

    // ---- Unicode 运算符（× ÷）----

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

    // ---- 大数处理 ----

    [Fact]
    public void TryEvaluate_LargeNumbers()
    {
        BeeXExpression.TryEvaluate("999999*999999", out var value).Should().BeTrue();
        value.Should().Be(999998000001);
    }

    // ---- 空白处理 ----

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

    // ---- 小数与逗号小数点 ----

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

    // ---- 一元正负号 ----

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

    // ---- 浮点精度 ----

    [Fact]
    public void TryEvaluate_FractionalDivision()
    {
        BeeXExpression.TryEvaluate("7/2", out var value).Should().BeTrue();
        value.Should().Be(3.5);
    }
}
