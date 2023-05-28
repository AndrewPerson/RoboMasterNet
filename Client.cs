using System.Text;
using System.Net.Sockets;

using OpenCvSharp;

using FluentFTP;

using ChassisSpeedStruct = RoboMaster.ChassisSpeed;
using ChassisPositionStruct = RoboMaster.ChassisPosition;
using ChassisAttitudeStruct = RoboMaster.ChassisAttitude;
using ChassisStatusStruct = RoboMaster.ChassisStatus;

namespace RoboMaster;

public class RoboMasterClient : IDisposable
{
    public const int VIDEO_PORT = 40921;
    public const int AUDIO_PORT = 40922;
    public const int CONTROL_PORT = 40923;
    public const int PUSH_PORT = 40924;
    public const int EVENT_PORT = 40925;
    public const int IP_PORT = 40926;

    public Feed<ChassisPosition> ChassisPosition { get; } = new ();
    public Feed<ChassisAttitude> ChassisAttitude { get; } = new ();
    public Feed<ChassisStatus> ChassisStatus { get; } = new ();

    public Feed<Line> Line { get; } = new ();
    /// <summary>
    /// This does not automatically enable marker detection.
    /// </summary>
    public Feed<Marker[]> Markers { get; } = new ();

    private Socket? pushSocket = null;
    private Task pushTask;

    public Feed<Mat> Video { get; } = new ();
    private VideoCapture? videoCapture = null;
    private Mat? videoFrame = null;
    private IDisposable? videoFrameReplacerSubscription = null;
    private Task videoTask;

    private string ip;
    private Socket socket;
    private AsyncFtpClient ftp;

    private AsyncQueue<(string, TaskCompletionSource<ResponseData>)> commandQueue = new ();
    private TaskCompletionSource commandDispatcherBlocker = new ();
    private Task commandDispatcherTask;

    public static async Task<RoboMasterClient> Connect(string ip, int timeout = 5000)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
        var ftp = new AsyncFtpClient(ip);

        await Task.WhenAll(
            socket.ConnectAsync(ip, CONTROL_PORT, new CancellationTokenSource(timeout).Token).AsTask(),
            ftp.AutoConnect(new CancellationTokenSource(timeout).Token)
        );

        return new RoboMasterClient(ip, socket, ftp);
    }

    private RoboMasterClient(string ip, Socket socket, AsyncFtpClient ftp)
    {
        this.ip = ip;
        this.socket = socket;
        this.ftp = ftp;

        ChassisPosition.OnHasObservers += () => Task.Run(() => SetChassisPushRate(positionFreq: 1));
        ChassisPosition.OnNoObservers += () => Task.Run(() => SetChassisPushRate(positionFreq: 0));

        ChassisAttitude.OnHasObservers += () => Task.Run(() => SetChassisPushRate(attitudeFreq: 1));
        ChassisAttitude.OnNoObservers += () => Task.Run(() => SetChassisPushRate(attitudeFreq: 0));

        ChassisStatus.OnHasObservers += () => Task.Run(() => SetChassisPushRate(statusFreq: 1));
        ChassisStatus.OnNoObservers += () => Task.Run(() => SetChassisPushRate(statusFreq: 0));

        Line.OnHasObservers += () => Task.Run(() => LineRecognitionEnabled(true));
        Line.OnNoObservers += () => Task.Run(() => LineRecognitionEnabled(false));

        Video.OnHasObservers += () =>
        {
            videoFrameReplacerSubscription = Video.Subscribe(ReplacePrevVideoFrame);
            Task.Run(() => VideoPushEnabled(true));
        };

        Video.OnNoObservers += () =>
        {
            videoFrameReplacerSubscription?.Dispose();
            Task.Run(() => VideoPushEnabled(false));
        };

        commandDispatcherTask = Task.Run(DispatchCommands);
        pushTask = Task.Run(ListenForPush);
        videoTask = Task.Run(ListenForVideo);

        Task.Run(() => Do("command"));
    }

    public void Dispose()
    {
        socket.Dispose();
        ftp.Dispose();

        pushSocket?.Dispose();
        pushTask.Dispose();

        videoCapture?.Dispose();
        videoFrame?.Dispose();
        videoFrameReplacerSubscription?.Dispose();
        videoTask.Dispose();

        commandDispatcherTask.Dispose();
    }

    private async Task DispatchCommands()
    {
        await foreach (var (command, resultSource) in commandQueue)
        {
            var result = await DoUnsafe(command);
            resultSource.SetResult(result);
        }
    }

    private async Task ListenForPush()
    {
        pushSocket = new Socket(SocketType.Dgram, ProtocolType.IPv4);
        await pushSocket.ConnectAsync(ip, PUSH_PORT);

        while (true)
        {
            var response = "";

            do
            {
                var buffer = new byte[512];
                var received = await pushSocket.ReceiveAsync(buffer, SocketFlags.None);
                response += Encoding.UTF8.GetString(buffer, 0, received);
            }
            while (!response.EndsWith(";"));

            var data = ResponseData.Parse(response);

            // Data format: <topic> push <subtopic> <data...>

            if (data.Data[0] == "chassis")
            {
                if (data.Data[2] == "position")
                {
                    ChassisPosition.Notify(ChassisPositionStruct.Parse(
                        data with { Data = data.Data[3..] }
                    ));
                }
                else if (data.Data[2] == "attitude")
                {
                    ChassisAttitude.Notify(ChassisAttitudeStruct.Parse(
                        data with { Data = data.Data[3..] }
                    ));
                }
                else if (data.Data[2] == "status")
                {
                    ChassisStatus.Notify(ChassisStatusStruct.Parse(
                        data with { Data = data.Data[3..] }
                    ));
                }
                else
                {
                    Console.WriteLine($"Unknown chassis push: {data}");
                }
            }
            else if (data.Data[0] == "AI")
            {
                if (data.Data[2] == "line")
                {
                    Line.Notify(RoboMaster.Line.Parse(data with { Data = data.Data[3..] }));
                }
                else if (data.Data[2] == "marker")
                {
                    Markers.Notify(Marker.ParseMultiple(data with { Data = data.Data[3..] }));
                }
                else
                {
                    Console.WriteLine($"Unknown AI push: {data}");
                }
            }
            else
            {
                Console.WriteLine($"Unknown push: {data}");
            }
        }
    }

    private async Task ListenForVideo()
    {
        videoCapture = new VideoCapture($"tcp://{ip}:{VIDEO_PORT}");

        while (true)
        {
            var mat = new Mat();
            await Task.Run(() => videoCapture.Read(mat));
            Video.Notify(mat);
        }
    }

    private void ReplacePrevVideoFrame(Mat newFrame)
    {
        videoFrame?.Dispose();
        videoFrame = newFrame;
    }

    /// <summary>
    /// Sends a command to the RoboMaster and returns the response.
    /// THIS IS NOT THREAD SAFE. Use `Do` for thread safety.
    /// </summary>
    private async Task<ResponseData> DoUnsafe(string command)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(command), SocketFlags.None);
        
        var response = "";

        do
        {
            var buffer = new byte[512];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None);
            response += Encoding.UTF8.GetString(buffer, 0, received);
        }
        while (!response.EndsWith(";"));

        Console.WriteLine(response);

        return ResponseData.Parse(response);
    }

    public async Task<ResponseData> Do(params CommandArg[] args)
    {
        var resultSource = new TaskCompletionSource<ResponseData>();

        var command = string.Join(" ", args.Select(arg => arg.arg));

        commandQueue.Enqueue((command, resultSource));
        return await resultSource.Task;
    }

    public async Task<string> Version() => (await Do("version")).Data[0];

    public Task SetMode(Mode mode) =>
        Do("robot", "mode", mode);

    public async Task<Mode> GetMode() =>
        EnumExtensions.ParseDescription<Mode>((await Do("robot", "mode", "?")).Data[0]);

    public Task SetSpeed(float forwards, float right, float clockwise) =>
        Do("chassis", "speed", "x", forwards, "y", right, "z", clockwise);

    public Task SetWheelSpeed(float speed) => SetWheelSpeed(speed, speed, speed, speed);

    public Task SetWheelSpeed(float right, float left) => SetWheelSpeed(right, left, right, left);

    public Task SetWheelSpeed(float frontRight, float frontLeft, float backRight, float backLeft) =>
        Do(
            "chassis", "wheel",
            "w1", frontRight,
            "w2", frontLeft,
            "w3", backLeft,
            "w4", backRight
        );

    public async Task<ChassisSpeed> GetSpeed() =>
        ChassisSpeedStruct.Parse(await Do("chassis", "speed", "?"));

    /// <summary>
    /// NOTE: This does *not* wait until the robot has finished moving.
    /// </summary>
    public async Task Move(float forwards, float right, float clockwise, float? speed = null, float? rotationSpeed = null)
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

        await Do(args.ToArray());
    }

    public async Task<ChassisPosition> GetPosition() =>
        ChassisPositionStruct.Parse(await Do("chassis", "position", "?"));

    public async Task<ChassisAttitude> GetAttitude() =>
        ChassisAttitudeStruct.Parse(await Do("chassis", "attitude", "?"));

    public async Task<ChassisStatus> GetStatus() =>
        ChassisStatusStruct.Parse(await Do("chassis", "status", "?"));

    /// <summary>
    /// Allowed frequencies are 0, 1, 5, 10, 20, 30, 50.
    /// 0 turns pushing off for that stream.
    /// </summary>
    public async Task SetChassisPushRate(int? positionFreq = null, int? attitudeFreq = null, int? statusFreq = null)
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

        await Do(args.ToArray());
    }

    public Task SetLEDs(LEDComp comp, int r, int g, int b, LEDEffect effect = LEDEffect.Solid) =>
        Do(
            "led", "control",
            "comp", comp,
            "r", r, "g", g, "b", b,
            "effect", effect
        );

    public Task VideoPushEnabled(bool enabled = true) =>
        Do("stream", enabled ? EnabledState.On : EnabledState.Off);

    public Task UploadAudio(string filename, string id)
    {
        var uploadFilePath = $"/python/sdk_audio_{id}.wav";

        return ftp.UploadFile(filename, uploadFilePath);
    }

    public Task PlayAudio(string id)
    {
        throw new NotImplementedException();
    }

    public Task IrEnabled(bool enabled = true) =>
        Do("ir_distance_sensor", "measure", enabled ? EnabledState.On : EnabledState.Off);

    public async Task<int> GetIRDistance(int irId)
    {
        var response = await Do("ir_distance_sensor", "distance", irId, "?");
        return int.Parse(response.Data[0]);
    }

    public Task SetLineRecognitionColour(LineColour lineColour) =>
        Do("AI", "attribute", "line_color", lineColour);

    public Task LineRecognitionEnabled(bool enabled = true) =>
        Do("AI", "push", "line", enabled ? EnabledState.On : EnabledState.Off);

    public Task SetMarkerRecognitionColour(MarkerColour markerColour) =>
        Do("AI", "attribute", "marker_color", markerColour);

    public Task SetMarkerRecognitionDist(float dist) =>
        Do("AI", "attribute", "marker_dist", dist);

    public Task SetVisionProcessing(VisionProcessing? processing)
    {
        if (processing == null) return Do(
            "AI", "push",
            VisionProcessing.Marker, EnabledState.Off,
            VisionProcessing.People, EnabledState.Off,
            VisionProcessing.Pose, EnabledState.Off,
            VisionProcessing.Robot, EnabledState.Off
        );

        return Do("AI", "push", processing.Value, EnabledState.On);
    }
}
