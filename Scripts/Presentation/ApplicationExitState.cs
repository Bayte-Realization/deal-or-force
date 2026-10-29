namespace DealOrForce;

/// <summary>One application shutdown, preserving the first nonzero exit reason.</summary>
public sealed class ApplicationExitState
{
    public bool Requested { get; private set; }
    public int ExitCode { get; private set; }

    public bool Request(int code)
    {
        if (Requested)
        {
            if (ExitCode == 0 && code != 0) ExitCode = code;
            return false;
        }
        Requested = true;
        ExitCode = code;
        return true;
    }
}
