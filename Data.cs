namespace RoboMaster;

public enum EnabledState
{
    [SerialisedValue("on")]
    On,
    [SerialisedValue("off")]
    Off
}

public enum Mode
{
    [SerialisedValue("chassis_lead")]
    ChassisLead,
    [SerialisedValue("gimbal_lead")]
    GimbalLead,
    [SerialisedValue("free")]
    Free
}

public enum LEDComp
{
    [SerialisedValue("all")]
    All,
    [SerialisedValue("top_all")]
    TopAll,
    [SerialisedValue("top_right")]
    TopRight,
    [SerialisedValue("top_left")]
    TopLeft,
    [SerialisedValue("bottom_all")]
    BottomAll,
    [SerialisedValue("bottom_front")]
    BottomFront,
    [SerialisedValue("bottom_back")]
    BottomBack,
    [SerialisedValue("bottom_left")]
    BottomLeft,
    [SerialisedValue("bottom_right")]
    BottomRight
}

public enum LEDEffect
{
    [SerialisedValue("off")]
    Off,
    [SerialisedValue("solid")]
    Solid,
    [SerialisedValue("blink")]
    Blink,
    [SerialisedValue("pulse")]
    Pulse,
    [SerialisedValue("scrolling")]
    Scrolling
}

public enum GripperStatus
{
    [SerialisedValue("0")]
    Closed,
    [SerialisedValue("1")]
    PartiallyOpen,
    [SerialisedValue("2")]
    Open
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
    [SerialisedValue("red")]
    Red,
    [SerialisedValue("blue")]
    Blue,
    [SerialisedValue("green")]
    Green
}

public enum MarkerColour
{
    [SerialisedValue("red")]
    Red,
    [SerialisedValue("blue")]
    Blue
}

public enum VisionProcessing
{
    [SerialisedValue("people")]
    People,
    [SerialisedValue("pose")]
    Pose,
    [SerialisedValue("marker")]
    Marker,
    [SerialisedValue("robot")]
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

/// <summary>
/// Used for implicit conversion of primitive data types to the text-based format the robot expects.
/// i.e. <c>CommandArg arg = 1;</c> will set <c>arg</c> to <c>"1"</c>.
/// </summary>
public record struct CommandArg(string arg)
{
    public static implicit operator CommandArg(string arg) => new(arg);
    public static implicit operator CommandArg(Enum arg) => new(arg.GetSerialisedValue());
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

    public string GetString(int index)
    {
        return Data[index];
    }

    public T GetEnum<T>(int index) where T : Enum
    {
        return EnumExtensions.ParseSerialisedValue<T>(Data[index]);
    }

    public int GetInt(int index)
    {
        return int.Parse(Data[index]);
    }

    public float GetFloat(int index)
    {
        return float.Parse(Data[index]);
    }

    public bool GetBool(int index)
    {
        return Data[index] != "0";
    }
}

public record struct WheelSpeed(float FrontRight, float FrontLeft, float BackRight, float BackLeft);

public record struct ChassisSpeed(float Z, float X, float Clockwise, WheelSpeed Wheels)
{
    public static ChassisSpeed Parse(ResponseData data)
    {
        return new ChassisSpeed(
            Z:         data.GetFloat(0),
            X:         data.GetFloat(1),
            Clockwise: data.GetFloat(2),
            Wheels:    new WheelSpeed(
                FrontRight: data.GetFloat(3),
                FrontLeft:  data.GetFloat(4),
                BackRight:  data.GetFloat(5),
                BackLeft:   data.GetFloat(6)
            )
        );
    }
}

public record struct ChassisPosition(float Z, float X, float? Clockwise)
{
    public static ChassisPosition Parse(ResponseData data)
    {
        return new ChassisPosition(
            Z: data.GetFloat(0),
            X: data.GetFloat(1),
            Clockwise: data.Data.Length == 3 ? data.GetFloat(2) : null
        );
    }
}

public record struct ChassisAttitude(float Pitch, float Roll, float Yaw)
{
    public static ChassisAttitude Parse(ResponseData data)
    {
        return new ChassisAttitude(
            Pitch: data.GetFloat(0),
            Roll:  data.GetFloat(1),
            Yaw:   data.GetFloat(2)
        );
    }
}

public record struct ChassisStatus(bool Static, bool UpHill, bool DownHill, bool OnSlope, bool PickUp, bool Slip,
                                        bool ImpactX, bool ImpactY, bool ImpactZ, bool RollOver, bool HillStatic)
{
    public static ChassisStatus Parse(ResponseData data)
    {
        return new ChassisStatus(
            Static:     data.GetBool(0),
            UpHill:     data.GetBool(1),
            DownHill:   data.GetBool(2),
            OnSlope:    data.GetBool(3),
            PickUp:     data.GetBool(4),
            Slip:       data.GetBool(5),
            ImpactX:    data.GetBool(6),
            ImpactY:    data.GetBool(7),
            ImpactZ:    data.GetBool(8),
            RollOver:   data.GetBool(9),
            HillStatic: data.GetBool(10)
        );
    }
}

public record struct Line(LineType Type, Point[] Points)
{
    public static Line Parse(ResponseData data)
    {
        var pointCount = (data.Data.Length - 1) / 4;

        var lineType = data.GetInt(0) switch
        {
            0 => LineType.None,
            1 => LineType.Straight,
            2 => LineType.Fork,
            3 => LineType.Intersection,
            _ => throw new ArgumentOutOfRangeException()
        };

        var points = new Point[pointCount];

        for (var i = 0; i < pointCount; i++)
        {
            points[i] = new Point(
                X:         data.GetFloat(i * 4 + 1),
                Y:         data.GetFloat(i * 4 + 2),
                Tangent:   data.GetFloat(i * 4 + 3),
                Curvature: data.GetFloat(i * 4 + 4)
            );
        }

        return new Line(lineType, points);
    }
}

public record struct Point(float X, float Y, float Tangent, float Curvature);

/// <summary>
/// Represents a single marker data point.
public record struct MarkerData
{

    public bool IsSymbolic => symbolicData != null;
    public bool IsInt => intData != null;
    public bool IsChar => charData != null;

    public MarkerSymbolicData Symbol
    {
        get
        {
            if (symbolicData == null)
            {
                throw new InvalidOperationException("Marker data is not symbolic");
            }

            return symbolicData.Value;
        }
    }

    public int Int
    {
        get
        {
            if (intData == null)
            {
                throw new InvalidOperationException("Marker data is not an int");
            }

            return intData.Value;
        }
    }

    public char Char
    {
        get
        {
            if (charData == null)
            {
                throw new InvalidOperationException("Marker data is not a char");
            }

            return charData.Value;
        }
    }

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
}

public record Marker(MarkerData Data, int X, int Y, int Width, int Height)
{
    public static Marker[] ParseMultiple(ResponseData data)
    {
        var markerCount = data.GetInt(0);
        var markers = new Marker[markerCount];

        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = new Marker(
                Data:   MarkerData.FromCode(data.GetInt(i * 5 + 1)),
                X:      data.GetInt(i * 5 + 2),
                Y:      data.GetInt(i * 5 + 3),
                Width:  data.GetInt(i * 5 + 4),
                Height: data.GetInt(i * 5 + 5)
            );
        }

        return markers;
    }
}
