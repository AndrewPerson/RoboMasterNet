using System.Text;
using System.Numerics;
using System.Net.Sockets;

using OpenCvSharp;

namespace RoboMaster;

public class RoboMasterClient : IDisposable
{
    public const string DIRECT_CONNECT_IP = "192.168.2.1";

    public const int VIDEO_PORT = 40921;
    public const int AUDIO_PORT = 40922;
    public const int CONTROL_PORT = 40923;
    public const int PUSH_PORT = 40924;
    public const int EVENT_PORT = 40925;
    public const int IP_PORT = 40926;

    public Feed<ChassisPosition> ChassisPosition { get; } = new();
    public Feed<ChassisAttitude> ChassisAttitude { get; } = new();
    public Feed<ChassisStatus> ChassisStatus { get; } = new();

    public Feed<Line> Line { get; } = new ();
    public Feed<Marker[]> Markers { get; } = new();

    private PushReceiver pushReceiver = new(PUSH_PORT);

    public Feed<Mat> Video { get; } = new();
    private VideoCapture? videoCapture = null;

    private string ip;
    private TcpClient client;

    private object videoThreadLock = new();
    private Thread? videoThread;

    private AsyncQueue<(string, TaskCompletionSource<ResponseData>, CancellationToken?)> commandQueue = new();

    public static async Task<RoboMasterClient> Connect(string ip, int timeout = 5000)
    {
        var client = new TcpClient();

        var cancellationTokenSource = new CancellationTokenSource(timeout);

        await client.ConnectAsync(ip, CONTROL_PORT, cancellationTokenSource.Token);

        cancellationTokenSource.Dispose();

        return new RoboMasterClient(ip, client);
    }

    private RoboMasterClient(string ip, TcpClient client)
    {
        this.ip = ip;
        this.client = client;

        commandQueue.Enqueue(("command;", new TaskCompletionSource<ResponseData>(), null));

        pushReceiver.Data.Subscribe(data =>
        {
            var topic = data.Data[0];
            var subject = data.Data[2];
            var parseData = data with { Data = data.Data[3..] };

            if (topic == "chassis")
            {
                if (subject == "position") ChassisPosition.Notify(RoboMaster.ChassisPosition.Parse(parseData));
                else if (subject == "attitude") ChassisAttitude.Notify(RoboMaster.ChassisAttitude.Parse(parseData));
                else if (subject == "status") ChassisStatus.Notify(RoboMaster.ChassisStatus.Parse(parseData));
                else Console.WriteLine($"Unknown chassis push: {data}");
            }
            else if (topic == "AI")
            {
                if (subject == "line") Line.Notify(RoboMaster.Line.Parse(parseData));
                else if (subject == "marker") Markers.Notify(Marker.ParseMultiple(parseData));
                else Console.WriteLine($"Unknown AI push: {data}");
            }
            else Console.WriteLine($"Unknown push: {data}");
        });

        var commandDispatcherThread = new Thread(DispatchCommands);
        commandDispatcherThread.IsBackground = true;
        commandDispatcherThread.Start();
    }

    public void Dispose()
    {
        client.Dispose();

        pushReceiver.Dispose();

        videoCapture?.Dispose();
    }

    private async void DispatchCommands()
    {
        await foreach (var (command, resultSource, cancellationToken) in commandQueue)
        {
            if (cancellationToken?.IsCancellationRequested ?? false)
            {
                resultSource.SetCanceled(cancellationToken.Value);
            }
            else
            {
                var result = await DoUnsafe(command);
                resultSource.SetResult(result);
            }
        }
    }

    private void ListenForVideo()
    {
        videoCapture = new VideoCapture($"tcp://{ip}:{VIDEO_PORT}");
        videoCapture.Set(VideoCaptureProperties.BufferSize, 4);

        while (true)
        {
            var mat = new Mat();
            videoCapture.Read(mat);
            Video.Notify(mat);

            mat.Dispose();
        }
    }

    /// <summary>
    /// THIS IS NOT THREAD SAFE. Use `Do` for thread safety.
    /// Sends a command to the RoboMaster and returns the response.
    /// </summary>
    private async Task<ResponseData> DoUnsafe(string command)
    {
        Console.WriteLine(command);

        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes(command));

        var response = "";

        do
        {
            var buffer = new byte[1];
            var received = await stream.ReadAsync(buffer);
            response += Encoding.UTF8.GetString(buffer, 0, received);
        }
        while (!response.EndsWith(";"));

        Console.WriteLine(response);

        return ResponseData.Parse(response);
    }

    public async Task<ResponseData> Do(params CommandArg[] args) => await Do(null, args);

    public async Task<ResponseData> Do(CancellationToken? cancellationToken = null, params CommandArg[] args)
    {
        var resultSource = new TaskCompletionSource<ResponseData>();

        var command = string.Join(" ", args.Select(arg => arg.arg)) + ";";

        commandQueue.Enqueue((command, resultSource, cancellationToken));
        return await resultSource.Task;
    }

    public async Task<string> Version(CancellationToken? cancellationToken = null) => (await Do(cancellationToken, "version")).Data[0];

    public async Task SetMode(Mode mode, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robot", "mode", mode);

    public async Task<Mode> GetMode(CancellationToken? cancellationToken = null) =>
        (await Do(cancellationToken, "robot", "mode", "?")).GetEnum<Mode>(0);

    public async Task SetSpeed(float forwards, float right, float clockwise, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "chassis", "speed", "x", forwards, "y", right, "z", clockwise);

    public async Task SetWheelSpeed(float speed, CancellationToken? cancellationToken = null) =>
        await SetWheelSpeed(speed, speed, speed, speed, cancellationToken);

    public async Task SetWheelSpeed(float right, float left, CancellationToken? cancellationToken = null) =>
        await SetWheelSpeed(right, left, right, left, cancellationToken);

    public async Task SetWheelSpeed(float frontRight, float frontLeft, float backRight, float backLeft, CancellationToken? cancellationToken = null) =>
        await Do(
            cancellationToken,
            "chassis", "wheel",
            "w1", frontRight,
            "w2", frontLeft,
            "w3", backLeft,
            "w4", backRight
        );

    public async Task<ChassisSpeed> GetSpeed(CancellationToken? cancellationToken = null) =>
        RoboMaster.ChassisSpeed.Parse(await Do(cancellationToken, "chassis", "speed", "?"));

    /// <summary>
    /// NOTE: This does *not* wait until the robot has finished moving.
    /// </summary>
    public async Task Move(float forwards, float right, float clockwise, float? speed = null, float? rotationSpeed = null, CancellationToken? cancellationToken = null)
    {
        var args = new List<CommandArg>
        {
            "chassis", "move",
            "x", forwards,
            "y", right,
            "z", clockwise
        };

        if (speed.HasValue)
        {
            args.Add("vxy");
            args.Add(speed.Value);
        }

        if (rotationSpeed.HasValue)
        {
            args.Add("vz");
            args.Add(rotationSpeed.Value);
        }

        await Do(cancellationToken, args.ToArray());
    }

    public async Task<ChassisPosition> GetPosition(CancellationToken? cancellationToken = null) =>
        RoboMaster.ChassisPosition.Parse(await Do(cancellationToken, "chassis", "position", "?"));

    public async Task<ChassisAttitude> GetAttitude(CancellationToken? cancellationToken = null) =>
        RoboMaster.ChassisAttitude.Parse(await Do(cancellationToken, "chassis", "attitude", "?"));

    public async Task<ChassisStatus> GetStatus(CancellationToken? cancellationToken = null) =>
        RoboMaster.ChassisStatus.Parse(await Do(cancellationToken, "chassis", "status", "?"));

    /// <summary>
    /// Allowed frequencies are 0, 1, 5, 10, 20, 30, 50.
    /// 0 turns pushing off for that stream.
    /// null leaves the current frequency unchanged.
    /// </summary>
    public async Task SetChassisPushRate(int? positionFreq = null, int? attitudeFreq = null, int? statusFreq = null, CancellationToken? cancellationToken = null)
    {
        if (positionFreq == null && attitudeFreq == null && statusFreq == null) throw new ArgumentException("At least one frequency must be set.");

        var positionArgs = positionFreq switch
        {
            null => new List<CommandArg>(),
            0 => new List<CommandArg> { "position", EnabledState.Off },
            int freq => new List<CommandArg> { "position", EnabledState.On, "pfreq", freq }
        };

        var attitudeArgs = attitudeFreq switch
        {
            null => new List<CommandArg>(),
            0 => new List<CommandArg> { "attitude", EnabledState.Off },
            int freq => new List<CommandArg> { "attitude", EnabledState.On, "afreq", freq }
        };

        var statusArgs = statusFreq switch
        {
            null => new List<CommandArg>(),
            0 => new List<CommandArg> { "status", EnabledState.Off },
            int freq => new List<CommandArg> { "status", EnabledState.On, "sfreq", freq }
        };

        var args = new List<CommandArg> { "chassis", "push" };
        args.AddRange(positionArgs);
        args.AddRange(attitudeArgs);
        args.AddRange(statusArgs);

        await Do(cancellationToken, args.ToArray());
    }

    public async Task SetLEDs(LEDComp comp, int r, int g, int b, LEDEffect effect = LEDEffect.Solid, CancellationToken? cancellationToken = null) =>
        await Do(
            cancellationToken,
            "led", "control",
            "comp", comp,
            "r", r, "g", g, "b", b,
            "effect", effect
        );

    public async Task SetVideoPushEnabled(bool enabled = true, CancellationToken? cancellationToken = null)
    {
        await Do(cancellationToken, "stream", enabled ? EnabledState.On : EnabledState.Off);
        
        if (enabled)
        {
            lock (videoThreadLock)
            {
                if (videoThread == null)
                {
                    videoThread = new Thread(ListenForVideo);
                    videoThread.IsBackground = true;
                    videoThread.Start();
                }
            }
        }
        else
        {
            lock (videoThreadLock)
            {
                videoThread?.Interrupt();
                videoThread = null;
            }
        }
    }

    public async Task SetIrEnabled(bool enabled = true, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "ir_distance_sensor", "measure", enabled ? EnabledState.On : EnabledState.Off);

    public async Task<float> GetIRDistance(int irId, CancellationToken? cancellationToken = null)
    {
        var response = await Do(cancellationToken, "ir_distance_sensor", "distance", irId, "?");
        return response.GetFloat(0);
    }

    public async Task MoveArm(float xDist, float yDist, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_arm", "move", "x", xDist, "y", yDist);

    public async Task SetArmPosition(float x, float y, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_arm", "moveto", "x", x, "y", y);

    public async Task RecenterArm(CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_arm", "recenter");

    public async Task StopRoboticArm(CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_arm", "stop");

    public async Task<Vector2> GetArmPosition(CancellationToken? cancellationToken = null)
    {
        var data = await Do(cancellationToken, "robotic_arm", "position", "?");

        return new Vector2(data.GetFloat(0), data.GetFloat(1));
    }

    // TODO Allow different levels of force when opening?
    // https://robomaster-dev.readthedocs.io/en/latest/text_sdk/protocol_api.html#mechanical-gripper-opening-control
    public async Task OpenGripper(CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_gripper", "open", 1);

    // TODO Allow different levels of force when closing?
    // https://robomaster-dev.readthedocs.io/en/latest/text_sdk/protocol_api.html#mechanical-gripper-opening-control
    public async Task CloseGripper(CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "robotic_gripper", "close", 1);

    public async Task<GripperStatus> GetGripperStatus(CancellationToken? cancellationToken = null) =>
        (await Do(cancellationToken, "robotic_gripper", "status", "?")).GetEnum<GripperStatus>(0);

    public async Task SetLineRecognitionColour(LineColour lineColour, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "AI", "attribute", "line_color", lineColour);

    public async Task SetLineRecognitionEnabled(bool enabled = true, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "AI", "push", "line", enabled ? EnabledState.On : EnabledState.Off);

    public async Task SetMarkerRecognitionColour(MarkerColour markerColour, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "AI", "attribute", "marker_color", markerColour);

    public async Task SetMarkerRecognitionDist(float dist, CancellationToken? cancellationToken = null) =>
        await Do(cancellationToken, "AI", "attribute", "marker_dist", dist);

    public async Task SetVisionProcessing(VisionProcessing? processing, CancellationToken? cancellationToken = null)
    {
        if (processing == null)
            await Do
            (
                cancellationToken,
                "AI", "push",
                VisionProcessing.Marker, EnabledState.Off,
                VisionProcessing.People, EnabledState.Off,
                VisionProcessing.Pose,   EnabledState.Off,
                VisionProcessing.Robot,  EnabledState.Off
            );
        else await Do(cancellationToken, "AI", "push", processing.Value, EnabledState.On);
    }
}
