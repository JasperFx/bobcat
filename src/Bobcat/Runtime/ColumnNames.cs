using System.Reflection;

namespace Bobcat.Runtime;

/// <summary>
/// What a table column is called for a given parameter or property: its <see cref="HeaderAttribute"/>
/// title when it has one, else its own name.
/// </summary>
/// <remarks>
/// One authority, because the question is asked from both directions and the answers have to agree:
/// <see cref="TableRunner"/> asks it of a row method's parameter to bind a cell, and
/// <see cref="SetVerificationComparer"/> asks it of a result type's property to decide which column
/// that property is compared under. A column titled one thing on the way in and another on the way
/// out would be two columns.
/// </remarks>
internal static class ColumnNames
{
    public static string Of(ParameterInfo parameter)
        => parameter.GetCustomAttribute<HeaderAttribute>()?.Name ?? parameter.Name ?? "";

    public static string Of(PropertyInfo property)
        => property.GetCustomAttribute<HeaderAttribute>()?.Name ?? property.Name;
}
