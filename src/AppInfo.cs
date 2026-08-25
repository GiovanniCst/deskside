// Product metadata, in one place so the About box, the window titles and the
// executable's version resource cannot drift apart.
using System;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Deskside")]
[assembly: AssemblyProduct("Deskside")]
[assembly: AssemblyDescription("Monitor and keyboard-layout control for docked Windows laptops")]
[assembly: AssemblyCompany("Giovanni J. Costantini")]
[assembly: AssemblyCopyright("Copyright © 2026 Giovanni J. Costantini. Apache License 2.0.")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: ComVisible(false)]

namespace Deskside
{
    public static class AppInfo
    {
        public const string Name = "Deskside";
        public const string Tagline = "Your desk, the way you left it.";
        public const string Author = "Giovanni J. Costantini";
        public const string AuthorUrl = "https://costantini.pw";
        public const string ProjectUrl = "https://github.com/GiovanniCst/deskside";
        public const string License = "Apache License 2.0";
        public const string LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0";

        public static string Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }
    }
}
