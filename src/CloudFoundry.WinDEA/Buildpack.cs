namespace CloudFoundry.WinDEA
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using CloudFoundry.Utilities;
    using YamlDotNet.RepresentationModel;
    using YamlDotNet.RepresentationModel.Serialization;

    public class Buildpack
    {
        public string Name
        {
            get 
            {
                return detectOutput.Trim();
            }
        }

        public string PhysicalPath
        {
            get
            {
                return this.path;
            }
        }

        private string detectOutput;
        private string path;
        private string appDir;
        private string cacheDir;
        private string logFile;

        public Buildpack(string path, string appDir, string cacheDir, string logFile)
        {
            this.path = path;
            this.appDir = appDir;
            this.cacheDir = cacheDir;
            this.logFile = logFile;
        }

        public bool Detect(CloudFoundry.WindowsPrison.Prison prison)
        {
            string exe = GetExecutable(Path.Combine(this.path, "bin"), "detect");

            string outputPath = Path.Combine(this.cacheDir, "detect.yml");
            string script = string.Format("{0} {1} > {2} 2>&1", exe, this.appDir, outputPath);

            Logger.Debug("Running detect script: {0}", script);

            Process process = prison.Execute(null, script, this.appDir, false, null, null, null, null);

            var startTs = DateTime.Now;
            while (prison.JobObject != null && prison.JobObject.ActiveProcesses > 0)
            {
                if ((DateTime.Now - startTs).TotalSeconds > 30)
                {
                    Logger.Debug("Staging's detect script timed out. Killing all job processes. Detect path: {0}", exe);
                    prison.JobObject.TerminateProcesses(-2);
                    break;
                }

                Thread.Sleep(100);
            }

            if (File.Exists(outputPath))
            {
                this.detectOutput = File.ReadAllText(outputPath);
                Logger.Debug("Detect output: {0}", this.detectOutput);
                File.Delete(outputPath);
            }
            else
            {
                Logger.Warning("Detect output missing. Detect yml path: {0}", outputPath);
            }

            if (process.ExitCode == 0)
            {
                return true;
            }
            else
            {
                Logger.Warning("Detect process exited with {0}", process.ExitCode);
                return false;
            }
        }

        public Process StartCompile(CloudFoundry.WindowsPrison.Prison prison) 
        {
            string exe = GetExecutable(Path.Combine(path, "bin"), "compile");
            string args = string.Format("{0} {1} >> {2} 2>&1", this.appDir, this.cacheDir, this.logFile);
            Logger.Debug("Running compile script {0} {1}", exe, args);
           
            var script = string.Format("{0} {1}", exe, args);

            Process process = prison.Execute(null, script, this.appDir, false, null, null, null, null);

            return process;
        }

        public ReleaseInfo GetReleaseInfo(CloudFoundry.WindowsPrison.Prison prison) 
        {
            string exe = GetExecutable(Path.Combine(this.path, "bin"), "release");

            string outputPath = Path.Combine(this.cacheDir, "release.yml");
            string script = string.Format("{0} {1} > {2} 2>&1", exe, this.appDir, outputPath);

            Process process = prison.Execute(null, script, this.appDir, false, null, null, null, null);
            
            process.WaitForExit(5000);

            string output = File.ReadAllText(outputPath);
            File.Delete(outputPath);
            using (var reader = new StringReader(output))
            {
                Deserializer deserializer = new Deserializer();
                return (ReleaseInfo)deserializer.Deserialize(reader, typeof(ReleaseInfo));
            }
        }

        private string GetExecutable(string path, string file)
        {
            string[] pathExt = Environment.GetEnvironmentVariable("PATHEXT").Split(';');
            foreach (string ext in pathExt)
            {
                if(File.Exists(Path.Combine(path, file+ext)))
                {
                    return Path.Combine(path, file+ext);
                }
            }
            throw new Exception("No executable found");
        }
    }

    public class ReleaseInfo
    {
        [Obsolete("'default_process_type' key is deprecated, please use 'default_process_types' key (note the s at the end)")]
        [YamlAlias("default_process_type")]
        public DefaultProcessType defaultProcessType { get; set; }

        [YamlAlias("default_process_types")]
        public Dictionary<string, string> defaultProcessTypes { get; set; }

    }

    public class DefaultProcessType
    {
        [YamlAlias("web")]
        public string Web { get; set; }
    }
}
