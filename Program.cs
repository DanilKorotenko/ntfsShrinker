using ntfsShrinker;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static int Main(string[] args)
    {
        //var app = new NtfsShrinkerCli();
        //int result = app.Run(args);

        string driveLetter = args[0];

        while (true)
        {
            NtfsShrinkerController.Info info = NtfsShrinkerController.GetInfo(driveLetter[0]);
            Console.WriteLine(info);

            NtfsShrinkerController.ShrinkVolume(driveLetter[0], 1);

            Console.ReadLine();
        }
        return 0;
    }
}
