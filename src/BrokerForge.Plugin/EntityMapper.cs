using System;
using System.Collections.Generic;
using System.Globalization;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin;

/// <summary>Converts between Dataverse attribute values and canonical <see cref="FieldValue"/>s (invariant strings).</summary>
public static class EntityMapper
{
    public static List<FieldValue> ToFields(Entity entity, string primaryIdAttribute)
    {
        var fields = new List<FieldValue>();
        foreach (var attribute in entity.Attributes)
        {
            if (!string.Equals(attribute.Key, primaryIdAttribute, StringComparison.Ordinal))
            {
                fields.Add(ToField(attribute.Key, attribute.Value));
            }
        }

        return fields;
    }

    public static FieldValue ToField(string name, object? value)
    {
        switch (value)
        {
            case null: return new FieldValue(name, FieldType.String, null);
            case string s: return new FieldValue(name, FieldType.String, s);
            case int i: return new FieldValue(name, FieldType.Int, FieldValue.Format(i));
            case long l: return new FieldValue(name, FieldType.Long, FieldValue.Format(l));
            case decimal d: return new FieldValue(name, FieldType.Decimal, FieldValue.Format(d));
            case double f: return new FieldValue(name, FieldType.Double, f.ToString("R", CultureInfo.InvariantCulture));
            case bool b: return new FieldValue(name, FieldType.Bool, b ? "true" : "false");
            case DateTime dt: return new FieldValue(name, FieldType.DateTime, dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            case Guid g: return new FieldValue(name, FieldType.Guid, g.ToString("D"));
            case OptionSetValue o: return new FieldValue(name, FieldType.OptionSet, FieldValue.Format(o.Value));
            case Money m: return new FieldValue(name, FieldType.Money, FieldValue.Format(m.Value));
            case EntityReference r: return new FieldValue(name, FieldType.Guid, r.Id.ToString("D"));
            default:
                throw new MappingException(name, "Attribute type " + value.GetType().Name + " is not supported by the canonical contract.");
        }
    }

    public static object? FromField(FieldValue field)
    {
        if (field.Value == null)
        {
            return null;
        }

        var v = field.Value;
        var c = CultureInfo.InvariantCulture;
        try
        {
            switch (field.Type)
            {
                case FieldType.String: return v;
                case FieldType.Int: return int.Parse(v, NumberStyles.Integer, c);
                case FieldType.Long: return long.Parse(v, NumberStyles.Integer, c);
                case FieldType.Decimal: return decimal.Parse(v, NumberStyles.Number, c);
                case FieldType.Double: return double.Parse(v, NumberStyles.Float, c);
                case FieldType.Bool: return bool.Parse(v);
                case FieldType.DateTime: return DateTime.Parse(v, c, DateTimeStyles.RoundtripKind);
                case FieldType.Date: return DateTime.ParseExact(v, "yyyy-MM-dd", c, DateTimeStyles.None);
                case FieldType.Guid: return Guid.Parse(v);
                case FieldType.OptionSet: return new OptionSetValue(int.Parse(v, NumberStyles.Integer, c));
                case FieldType.Money: return new Money(decimal.Parse(v, NumberStyles.Number, c));
                default: throw new MappingException(field.Name, "Unknown field type " + field.TypeName + ".");
            }
        }
        catch (FormatException ex)
        {
            throw new MappingException(field.Name, "Value is not a valid " + field.TypeName + ".", ex);
        }
    }

    /// <summary>
    /// Builds a virtual table row. Sets the primary key by convention (&lt;table&gt;id).
    /// Phase 1: read PrimaryIdAttribute from table metadata instead of assuming the convention.
    /// </summary>
    public static Entity ToEntity(string table, CanonicalRow row)
    {
        var id = Guid.Parse(row.Id);
        var entity = new Entity(table, id);
        entity[table + "id"] = id;
        foreach (var field in row.Fields)
        {
            entity[field.Name] = FromField(field);
        }

        return entity;
    }
}
