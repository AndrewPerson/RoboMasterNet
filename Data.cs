using System.ComponentModel;
using System.Reflection;

namespace RoboMaster;

public enum EnabledState
{
    [Description("on")]
    On,
    [Description("off")]
    Off
}

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

public enum LineType
{
    None,
    Straight,
    Fork,
    Intersection
}

public enum LineColour
{
    [Description("red")]
    Red,
    [Description("blue")]
    Blue,
    [Description("green")]
    Green
}

public enum MarkerColour
{
    [Description("red")]
    Red,
    [Description("blue")]
    Blue
}

public enum VisionProcessing
{
    [Description("people")]
    People,
    [Description("pose")]
    Pose,
    [Description("marker")]
    Marker,
    [Description("robot")]
    Robot
}

public enum MarkerSymbolicData
{
    Stop,
    TurnLeft,
    TurnRight,
    MoveForward,
    RedHeart
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

public record struct CommandArg(string arg)
{
    public static implicit operator CommandArg(string arg) => new(arg);
    public static implicit operator CommandArg(Enum arg) => new(arg.GetDescription());
    public static implicit operator CommandArg(int arg) => new(arg.ToString());
    public static implicit operator CommandArg(float arg) => new(arg.ToString());
    public static implicit operator CommandArg(bool arg) => new(arg ? "on" : "off");
}

public record struct ResponseData(string[] Data)
{
    public static ResponseData Parse(string data)
    {
        var parts = data.TrimEnd(';').Split(' ');
        return new ResponseData(parts);
    }
}

public record struct WheelSpeed(float FrontRight, float FrontLeft, float BackRight, float BackLeft);

public record struct ChassisSpeed(float Z, float X, float Clockwise, WheelSpeed Wheels)
{
    public static ChassisSpeed Parse(ResponseData data)
    {
        return new ChassisSpeed(
            Z:         float.Parse(data.Data[0]),
            X:         float.Parse(data.Data[1]),
            Clockwise: float.Parse(data.Data[2]),
            Wheels:    new WheelSpeed(
                FrontRight: float.Parse(data.Data[3]),
                FrontLeft:  float.Parse(data.Data[4]),
                BackRight:  float.Parse(data.Data[5]),
                BackLeft:   float.Parse(data.Data[6])
            )
        );
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

public record struct Line(LineType Type, Point[] Points)
{
    public static Line Parse(ResponseData data)
    {
        var pointCount = int.Parse(data.Data[0]);

        var lineType = int.Parse(data.Data[1]) switch
        {
            0 => LineType.None,
            1 => LineType.Straight,
            2 => LineType.Fork,
            3 => LineType.Intersection,
            _ => throw new ArgumentOutOfRangeException()
        };

        var points = new Point[pointCount];

        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Point(
                X:         int.Parse(data.Data[i * 4 + 2]),
                Y:         int.Parse(data.Data[i * 4 + 3]),
                Tangent:   int.Parse(data.Data[i * 4 + 4]),
                Curvature: int.Parse(data.Data[i * 4 + 5])
            );
        }

        return new Line(lineType, points);
    }
}

public record struct Point(int X, int Y, int Tangent, int Curvature);

public record struct MarkerData
{
    private MarkerSymbolicData? symbolicData;
    private int? intData;
    private char? charData;

    private MarkerData(MarkerSymbolicData symbolicData)
    {
        this.symbolicData = symbolicData;
        this.intData = null;
        this.charData = null;
    }

    private MarkerData(int intData)
    {
        this.symbolicData = null;
        this.intData = intData;
        this.charData = null;
    }

    private MarkerData(char charData)
    {
        this.symbolicData = null;
        this.intData = null;
        this.charData = charData;
    }

    public static MarkerData FromCode(int code)
    {
        return code switch
        {
            1 => MarkerSymbolicData.Stop,
            4 => MarkerSymbolicData.TurnLeft,
            5 => MarkerSymbolicData.TurnRight,
            6 => MarkerSymbolicData.MoveForward,
            8 => MarkerSymbolicData.RedHeart,
            <= 19 => code - 10,
            <= 45 => (char) (code - 20 + 'A'),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Invalid marker code")
        };
    }

    public static implicit operator MarkerData(MarkerSymbolicData symbolicData) => new(symbolicData);
    public static implicit operator MarkerData(int intData) => new(intData);
    public static implicit operator MarkerData(char charData) => new(charData);

    public static implicit operator MarkerSymbolicData?(MarkerData data) => data.symbolicData;
    public static implicit operator int?(MarkerData data) => data.intData;
    public static implicit operator char?(MarkerData data) => data.charData;
}

public record Marker(MarkerData Data, int X, int Y, int Width, int Height)
{
    public static Marker[] ParseMultiple(ResponseData data)
    {
        var markerCount = int.Parse(data.Data[0]);
        var markers = new Marker[markerCount];

        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = new Marker(
                Data:   MarkerData.FromCode(int.Parse(data.Data[i * 5 + 1])),
                X:      int.Parse(data.Data[i * 5 + 2]),
                Y:      int.Parse(data.Data[i * 5 + 3]),
                Width:  int.Parse(data.Data[i * 5 + 4]),
                Height: int.Parse(data.Data[i * 5 + 5])
            );
        }

        return markers;
    }
}
