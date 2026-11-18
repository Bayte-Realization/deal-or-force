using System;

static class Program
{
    static void Main(string[] args)
    {
        if (SuiteRegistry.TryDispatch(args)) return;
        SuiteRegistry.RunDefault();
    }
}
