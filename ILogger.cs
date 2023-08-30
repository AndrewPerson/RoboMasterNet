namespace RoboMaster;

public interface ILogger
{
    public void LogInfo(string message);

    public void LogWarning(string message);

    public void LogError(string message);
}