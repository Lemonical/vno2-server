using System.Globalization;

namespace VNO2.Server.Domain;

public sealed class SafeArithmetic
{
    private readonly string expression;
    private int position;
    private int depth;
    private int operations;

    private SafeArithmetic(string expression) => this.expression = expression;

    public static decimal Evaluate(string expression)
    {
        if (expression.Length is 0 or > 200) throw new DomainException("invalid_expression", "Expressions must contain 1 to 200 characters");
        try
        {
            var parser = new SafeArithmetic(expression);
            var value = parser.Sum();
            parser.Space();
            if (parser.position != expression.Length) throw new DomainException("invalid_expression", "Unexpected arithmetic token");
            return value;
        }
        catch (OverflowException) { throw new DomainException("arithmetic_overflow", "The result is outside the supported numeric range"); }
        catch (DivideByZeroException) { throw new DomainException("division_by_zero", "Division by zero is not allowed"); }
    }

    private decimal Sum()
    {
        var value = Product();
        while (true)
        {
            if (Take('+')) { Count(); value += Product(); }
            else if (Take('-')) { Count(); value -= Product(); }
            else return value;
        }
    }

    private decimal Product()
    {
        var value = Atom();
        while (true)
        {
            if (Take('*')) { Count(); value *= Atom(); }
            else if (Take('/')) { Count(); value /= Atom(); }
            else if (Take('%')) { Count(); value %= Atom(); }
            else return value;
        }
    }

    private decimal Atom()
    {
        if (++depth > 24) throw new DomainException("invalid_expression", "Arithmetic nesting is too deep");
        try
        {
            if (Take('+')) return Atom();
            if (Take('-')) return -Atom();
            if (Take('('))
            {
                var inner = Sum();
                if (!Take(')')) throw new DomainException("invalid_expression", "A closing parenthesis is missing");
                return inner;
            }
            Space();
            var start = position;
            while (position < expression.Length && (char.IsAsciiDigit(expression[position]) || expression[position] == '.')) position++;
            if (start == position || !decimal.TryParse(expression.AsSpan(start, position - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new DomainException("invalid_expression", "Expected a decimal number");
            return value;
        }
        finally { depth--; }
    }

    private bool Take(char token)
    {
        Space();
        if (position >= expression.Length || expression[position] != token) return false;
        position++;
        return true;
    }

    private void Count()
    {
        if (++operations > 64) throw new DomainException("invalid_expression", "Too many arithmetic operations");
    }

    private void Space()
    {
        while (position < expression.Length && char.IsWhiteSpace(expression[position])) position++;
    }
}
