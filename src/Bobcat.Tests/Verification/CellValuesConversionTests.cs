using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Verification;

/// <summary>
/// A domain type in a cell (bobcat#542): the conventions a type can offer — IParsable, a
/// TypeConverter, a ctor(string) — and <see cref="CellValues.Register{T}"/> for a type the suite
/// does not own. Each test that registers uses a type of its own, because a registration is global.
/// </summary>
public class CellValuesConversionTests
{
    [Fact]
    public void a_strong_typed_id_reads_through_its_IParsable()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        CellValues.Read<OrderId>("11111111-1111-1111-1111-111111111111").ShouldBe(new OrderId(id));
    }

    [Fact]
    public void an_explicitly_implemented_IParsable_is_found_too()
    {
        CellValues.Read<Sku>("ab-12").ShouldBe(new Sku("AB-12"));
    }

    [Fact]
    public void a_type_with_a_TypeConverter_reads_through_it()
    {
        CellValues.Read<Dimension>("1 x 2 x 36").ShouldBe(new Dimension(1, 2, 36));
    }

    [Fact]
    public void a_type_with_a_string_constructor_reads_through_it()
    {
        CellValues.Read<Email>("someone@example.com").Address.ShouldBe("someone@example.com");
    }

    [Fact]
    public void a_registration_reads_a_type_with_no_convention()
    {
        // #region register-reader
        CellValues.Register(text => new Colour(text.ToUpperInvariant()));
        // #endregion register-reader
        try
        {
            CellValues.Read<Colour>("red").ShouldBe(new Colour("RED"));
        }
        finally
        {
            CellValues.Unregister<Colour>();
        }
    }

    [Fact]
    public void a_registration_wins_over_the_type_s_own_conventions()
    {
        CellValues.Register(text => new Ticket("registered:" + text));
        try
        {
            CellValues.Read<Ticket>("T-1").ShouldBe(new Ticket("registered:T-1"));
        }
        finally
        {
            CellValues.Unregister<Ticket>();
        }

        CellValues.Read<Ticket>("T-1").ShouldBe(new Ticket("parsed:T-1"));
    }

    [Fact]
    public void a_registration_wins_over_a_built_in_reader()
    {
        CellValues.Register(text => text == "lots" ? 1000L : long.Parse(text, CultureInfo.InvariantCulture));
        try
        {
            CellValues.Read<long>("lots").ShouldBe(1000L);
        }
        finally
        {
            CellValues.Unregister<long>();
        }
    }

    [Fact]
    public void the_tokens_keep_their_meaning_for_a_domain_type()
    {
        CellValues.Read("NULL", typeof(OrderId?)).ShouldBeNull();
        CellValues.Read("", typeof(OrderId?)).ShouldBeNull();
        CellValues.Read("NULL", typeof(Email)).ShouldBeNull();
        CellValues.Read("EMPTY", typeof(Email)).ShouldBeNull();
        CellValues.Read<Email>("\"NULL\"").Address.ShouldBe("NULL");

        Should.Throw<BadCellException>(() => CellValues.Read<OrderId>("NULL"))
            .Message.ShouldContain("OrderId cannot be null");
    }

    [Fact]
    public void a_registration_never_sees_a_token()
    {
        var seen = new List<string>();
        CellValues.Register(text =>
        {
            seen.Add(text);
            return new Badge(text);
        });
        try
        {
            CellValues.Read("NULL", typeof(Badge)).ShouldBeNull();
            CellValues.Read<Badge>(" \"NULL\" ").ShouldBe(new Badge("NULL"));
            seen.ShouldBe(["NULL"]);
        }
        finally
        {
            CellValues.Unregister<Badge>();
        }
    }

    [Fact]
    public void a_conversion_that_throws_is_a_bad_cell_naming_the_cell_and_the_type()
    {
        var ex = Should.Throw<BadCellException>(() => CellValues.Read<OrderId>("not-a-guid"));

        ex.Message.ShouldContain("'not-a-guid'");
        ex.Message.ShouldContain("OrderId");
        ex.InnerException.ShouldBeOfType<FormatException>();
    }

    [Fact]
    public void a_type_with_no_way_to_read_it_says_what_would_fix_it()
    {
        var ex = Should.Throw<BadCellException>(() => CellValues.Read<Opaque>("x"));

        ex.Message.ShouldContain("CellValues.Register<Opaque>");
        ex.Message.ShouldContain("IParsable<Opaque>");
        ex.Message.ShouldContain("TypeConverter");
        ex.Message.ShouldContain("constructor taking a string");
    }

    [Fact]
    public void the_expected_side_reads_a_domain_type_the_same_way()
    {
        CellCheck.Check(new Dimension(1, 2, 36), "1 x 2 x 36").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(new Dimension(1, 2, 36), "1 x 2 x 40").Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check(new Sku("AB-12"), "ab-12").Outcome.ShouldBe(CheckOutcome.Match);

        CellValues.Register(text => new Shade(text.ToLowerInvariant()));
        try
        {
            CellCheck.Check(new Shade("teal"), "TEAL").Outcome.ShouldBe(CheckOutcome.Match);
        }
        finally
        {
            CellValues.Unregister<Shade>();
        }
    }

    [Fact]
    public void the_expected_side_says_why_a_domain_cell_is_invalid()
    {
        var cell = CellCheck.For("Order", new OrderId(Guid.NewGuid()), "not-a-guid");

        cell.Status.ShouldBe(ResultStatus.invalid);
        cell.Note.ShouldNotBeNull().ShouldContain("OrderId");
    }

    public readonly record struct OrderId(Guid Value) : IParsable<OrderId>
    {
        public static OrderId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out OrderId result)
        {
            var parsed = Guid.TryParse(s, provider, out var value);
            result = new OrderId(value);
            return parsed;
        }
    }

    public sealed record Sku(string Code) : IParsable<Sku>
    {
        static Sku IParsable<Sku>.Parse(string s, IFormatProvider? provider) => new(s.ToUpperInvariant());

        static bool IParsable<Sku>.TryParse(string? s, IFormatProvider? provider, out Sku result)
        {
            result = new Sku((s ?? "").ToUpperInvariant());
            return s != null;
        }
    }

    [TypeConverter(typeof(DimensionConverter))]
    public sealed record Dimension(int Width, int Height, int Length);

    public sealed class DimensionConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is not string text) return base.ConvertFrom(context, culture, value);

            var parts = text.Split('x', StringSplitOptions.TrimEntries)
                .Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return new Dimension(parts[0], parts[1], parts[2]);
        }
    }

    public sealed class Email(string address)
    {
        public string Address { get; } = address;
    }

    public sealed record Colour(string Name);

    public sealed record Badge(string Text);

    public sealed record Shade(string Name);

    public sealed record Opaque(int Value);

    public sealed record Ticket(string Value) : IParsable<Ticket>
    {
        public static Ticket Parse(string s, IFormatProvider? provider) => new("parsed:" + s);

        public static bool TryParse(string? s, IFormatProvider? provider, out Ticket result)
        {
            result = new Ticket("parsed:" + s);
            return s != null;
        }
    }
}
