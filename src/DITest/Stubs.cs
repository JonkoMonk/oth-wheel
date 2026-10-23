using System;
using System.IO;

// Stand-ins for the BepInEx / plugin bits Profile.cs uses, so it can be tested outside the game.
namespace BepInEx
{
    internal static class Paths
    {
        public static string ConfigPath = Path.Combine(Path.GetTempPath(), "othwheel-selftest");
    }
}

namespace OthWheel
{
    internal static class Plugin
    {
        public const string Version = "selftest";
        public static void Info(string s) => Console.WriteLine("  info: " + s);
        public static void Warn(string s) => Console.WriteLine("  warn: " + s);
        public static void Error(string s) => Console.WriteLine("  ERROR: " + s);
    }
}
