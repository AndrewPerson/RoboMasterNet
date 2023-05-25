using System.Text;
using System.Net.Sockets;
using System.Reactive.Subjects;

using OpenCvSharp;

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

    public const string ON = "on";
    public const string OFF = "off";

    public ReplaySubject<ChassisPosition> ChassisPosition { get; } = new ReplaySubject<ChassisPosition>(1);
    public ReplaySubject<ChassisAttitude> ChassisAttitude { get; } = new ReplaySubject<ChassisAttitude>(1);
    public ReplaySubject<ChassisStatus> ChassisStatus { get; } = new ReplaySubject<ChassisStatus>(1);
    private Socket? pushSocket = null;
    private Task pushTask;

    public ReplaySubject<Mat> Video { get; } = new ReplaySubject<Mat>(1);
    private VideoCapture? videoCapture = null;
    private Mat? videoFrame = null;
    private Task videoTask;

    private string ip;
    private Socket socket;

    private AsyncQueue<(Command, TaskCompletionSource<ResponseData>)> commandQueue = new( );
    private TaskCompletionSource commandDispatcherBlocker = new ();
    private Task commandDispatcherTask;

    public static async Task<RoboMasterClient> Connect(string ip, int timeout = 5000)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.IPv4);
        await socket.ConnectAsync(ip, CONTROL_PORT, new CancellationTokenSource(timeout).Token);
        return new RoboMasterClient(ip, socket);
    }

    private RoboMasterClient(string ip, Socket socket)
    {
        this.ip = ip;
        this.socket = socket;

        commandDispatcherTask = Task.Run(DispatchCommands);
        pushTask = Task.Run(ListenForPush);
        videoTask = Task.Run(ListenForVideo);
    }

    public void Dispose()
    {
        socket.Dispose();

        ChassisPosition.Dispose();
        ChassisAttitude.Dispose();
        ChassisStatus.Dispose();
        pushSocket?.Dispose();
        pushTask.Dispose();

        Video.Dispose();
        videoCapture?.Dispose();
        videoFrame?.Dispose();
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

            if (data.Data[0] == "chassis")
            {
                if (data.Data[1] == "position")
                {
                    ChassisPosition.OnNext(ChassisPositionStruct.Parse(
                        data with { Data = data.Data[2..] }
                    ));
                }
                else if (data.Data[1] == "attitude")
                {
                    ChassisAttitude.OnNext(ChassisAttitudeStruct.Parse(
                        data with { Data = data.Data[2..] }
                    ));
                }
                else if (data.Data[1] == "status")
                {
                    ChassisStatus.OnNext(ChassisStatusStruct.Parse(
                        data with { Data = data.Data[2..] }
                    ));
                }
            }
        }
    }

    private async Task ListenForVideo()
    {
        Video.Subscribe(mat =>
        {
            videoFrame?.Dispose();
            videoFrame = mat;
        });

        videoCapture = new VideoCapture($"tcp://{ip}:{VIDEO_PORT}");

        while (true)
        {
            var mat = new Mat();
            await Task.Run(() => videoCapture.Read(mat));
            Video.OnNext(mat);
        }
    }

    /// <summary>
    /// Sends a command to the RoboMaster and returns the response.
    /// THIS IS NOT THREAD SAFE. Use `Do` for thread safety.
    /// </summary>
    private async Task<ResponseData> DoUnsafe(Command command)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(command.ToString()), SocketFlags.None);
        
        var response = "";

        do
        {
            var buffer = new byte[512];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None);
            response += Encoding.UTF8.GetString(buffer, 0, received);
        }
        while (!response.EndsWith(";"));

        return ResponseData.Parse(response);
    }

    public Task<ResponseData> Do(string command, params string[] args) =>
        Do(new Command(command, args));

    public async Task<ResponseData> Do(Command command)
    {
        var resultSource = new TaskCompletionSource<ResponseData>();
        commandQueue.Enqueue((command, resultSource));
        return await resultSource.Task;
    }

    public async Task<string> Version() => (await Do("version")).Data[0];

    public async Task SetMode(Mode mode) =>
        await Do("robot", "mode", mode.GetDescription());

    public async Task<Mode> GetMode() =>
        EnumExtensions.ParseDescription<Mode>((await Do("robot", "mode", "?")).Data[0]);

    /// <summary>
    /// NOTE: This does *not* wait until the robot has finished moving.
    /// </summary>
    public async Task Move(float forwards, float right, float clockwise, float? speed = null, float? rotationSpeed = null)
    {
        var args = new List<string>
        {
            "move",
            "x", forwards.ToString(),
            "y", right.ToString(),
            "z", clockwise.ToString()
        };

        if (speed.HasValue)
        {
            args.Add("vxy");
            args.Add(speed.Value.ToString());
        }

        if (rotationSpeed.HasValue)
        {
            args.Add("vz");
            args.Add(rotationSpeed.Value.ToString());
        }

        await Do("chassis", args.ToArray());
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
    public async Task SetChassisPushRate(int positionFreq, int attitudeFreq, int statusFreq)
    {
        var positionArgs = positionFreq switch
        {
            0 => new List<string> { "position", OFF },
            var freq => new List<string> { "position", ON, "pfreq", freq.ToString() }
        };

        var attitudeArgs = attitudeFreq switch
        {
            0 => new List<string> { "attitude", OFF },
            var freq => new List<string> { "attitude", ON, "afreq", freq.ToString() }
        };

        var statusArgs = statusFreq switch
        {
            0 => new List<string> { "status", OFF },
            var freq => new List<string> { "status", ON, "sfreq", freq.ToString() }
        };

        var args = new List<string> { "push" };
        args.AddRange(positionArgs);
        args.AddRange(attitudeArgs);
        args.AddRange(statusArgs);

        await Do("chassis", args.ToArray());
    }
}
