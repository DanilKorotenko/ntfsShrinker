using ntfsShrinker;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static int Main(string[] args)
    {
        var app = new NtfsShrinkerCli();
        return app.Run(args);
    }
}
