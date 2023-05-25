using System.ComponentModel;
using System.Reflection;

namespace RoboMaster;

public enum Mode
{
    [Description("chassis_lead")]
    ChassisLead,
    [Description("gimbal_lead")]
    GimbalLead,
    [Description("free")]
    Free
}

public enum LEDComp
{
    [Description("all")]
    All,
    [Description("top_all")]
    TopAll,
    [Description("top_right")]
    TopRight,
    [Description("top_left")]
    TopLeft,
    [Description("bottom_all")]
    BottomAll,
    [Description("bottom_front")]
    BottomFront,
    [Description("bottom_back")]
    BottomBack,
    [Description("bottom_left")]
    BottomLeft,
    [Description("bottom_right")]
    BottomRight
}

public enum LEDEffect
{
    [Description("off")]
    Off,
    [Description("solid")]
    Solid,
    [Description("blink")]
    Blink,
    [Description("pulse")]
    Pulse,
    [Description("scrolling")]
    Scrolling
}

public static class EnumExtensions
{
    public static string GetDescription(this Enum e)
    {
        var attribute =
            e.GetType()
                .GetTypeInfo()
                .GetMember(e.ToString())
                .FirstOrDefault(member => member.MemberType == MemberTypes.Field)?
                .GetCustomAttributes(typeof(DescriptionAttribute), false)
                .SingleOrDefault()
                as DescriptionAttribute;

        return attribute?.Description ?? e.ToString();
    }

    public static T ParseDescription<T>(string description) where T : Enum
    {
        foreach (var value in Enum.GetValues(typeof(T)))
        {
            if (value is T enumValue)
            {
                if (enumValue.GetDescription() == description)
                {
                    return enumValue;
                }
            }
        }

        throw new ArgumentException($"No enum value with description {description} found.");
    }
}

public record struct Command(string CommandName, string[] Args)
{
    public override string ToString() => Args.Length == 0 ?
                                            $"{CommandName};" :
                                            $"{CommandName} {string.Join(" ", Args)};";
}

public record struct ResponseData(string[] Data)
{
    public static ResponseData Parse(string data)
    {
        var parts = data.TrimEnd(';').Split(' ');
        return new ResponseData(parts);
    }
}

public record struct ChassisPosition(float Z, float X, float? Clockwise)
{
    public static ChassisPosition Parse(ResponseData data)
    {
        return new ChassisPosition(
            Z: float.Parse(data.Data[0]),
            X: float.Parse(data.Data[1]),
            Clockwise: data.Data.Length == 3 ? float.Parse(data.Data[2]) : null
        );
    }
}

public record struct ChassisAttitude(float Pitch, float Roll, float Yaw)
{
    public static ChassisAttitude Parse(ResponseData data)
    {
        return new ChassisAttitude(
            Pitch: float.Parse(data.Data[0]),
            Roll:  float.Parse(data.Data[1]),
            Yaw:   float.Parse(data.Data[2])
        );
    }
}

public record struct ChassisStatus(bool Static, bool UpHill, bool DownHill, bool OnSlope, bool PickUp, bool Slip,
                                        bool ImpactX, bool ImpactY, bool ImpactZ, bool RollOver, bool HillStatic)
{
    public static ChassisStatus Parse(ResponseData data)
    {
        return new ChassisStatus(
            Static:     int.Parse(data.Data[0]) != 0,
            UpHill:     int.Parse(data.Data[1]) != 0,
            DownHill:   int.Parse(data.Data[2]) != 0,
            OnSlope:    int.Parse(data.Data[3]) != 0,
            PickUp:     int.Parse(data.Data[4]) != 0,
            Slip:       int.Parse(data.Data[5]) != 0,
            ImpactX:    int.Parse(data.Data[6]) != 0,
            ImpactY:    int.Parse(data.Data[7]) != 0,
            ImpactZ:    int.Parse(data.Data[8]) != 0,
            RollOver:   int.Parse(data.Data[9]) != 0,
            HillStatic: int.Parse(data.Data[10]) != 0
        );
    }
}