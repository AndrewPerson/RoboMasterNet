using System.Reflection;

namespace RoboMaster;

public static class EnumExtensions
{
    public static string GetSerialisedValue(this Enum e)
    {
        var attribute =
            e.GetType()
                .GetTypeInfo()
                .GetMember(e.ToString())
                .FirstOrDefault(member => member.MemberType == MemberTypes.Field)?
                .GetCustomAttributes(typeof(SerialisedValueAttribute), false)
                .SingleOrDefault()
                as SerialisedValueAttribute;

        return attribute?.Value ?? e.ToString();
    }

    public static T ParseSerialisedValue<T>(string description) where T : Enum
    {
        foreach (var value in Enum.GetValues(typeof(T)))
        {
            if (value is T enumValue)
            {
                if (enumValue.GetSerialisedValue() == description)
                {
                    return enumValue;
                }
            }
        }

        throw new ArgumentException($"No enum value with description {description} found.");
    }
}

[System.AttributeUsage(System.AttributeTargets.All, Inherited = false, AllowMultiple = false)]
sealed class SerialisedValueAttribute : System.Attribute
{
    readonly string value;
    
    // This is a positional argument
    public SerialisedValueAttribute(string value)
    {
        this.value = value;
    }
    
    public string Value
    {
        get { return value; }
    }
}
