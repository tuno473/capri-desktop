using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using Avalonia.Threading;
using SherpaOnnx;

namespace CapriDesktop
{
    // Her free voice, the same voice pack as Windows, running here with nothing else installed.
    internal sealed class FreeSpeaker : ISpeaker, IDisposable
    {
        private static readonly string[] Needed = { "model.onnx", "voices.bin", "tokens.txt", "lexicon-us-en.txt" };
        private const int HerOwn = 53;
        private readonly string folder;
        private readonly object gate = new object();
        private OfflineTts tts;

        internal FreeSpeaker(string data) { folder = VoicePack.Folder(data); }

        public string Name { get { return "Free voice"; } }
        public bool Ready { get { return Installed; } }

        internal bool Installed
        {
            get
            {
                foreach (string name in Needed) if (!File.Exists(Path.Combine(folder, name))) return false;
                return true;
            }
        }

        internal void Install(Action<string, long, long> progress)
        {
            string parent = Path.GetDirectoryName(folder), zip = folder + ".zip", staging = folder + ".new";
            Directory.CreateDirectory(parent);
            Engine.Fetch(VoicePack.Url, zip, VoicePack.Size, VoicePack.Sha, "her free voice", progress, delegate { return false; });
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zip, staging);
            foreach (string name in Needed) if (!File.Exists(Path.Combine(staging, name))) throw new IOException("The free voice download is missing files.");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
            File.Delete(zip);
        }

        public byte[] Speak(string text)
        {
            lock (gate)
            {
                if (!Installed) throw new VoiceException("Her free voice isn't in yet.");
                if (tts == null)
                {
                    OfflineTtsConfig config = new OfflineTtsConfig();
                    config.Model.NumThreads = Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2));
                    config.Model.Provider = "cpu";
                    config.Model.Kokoro.Model = Path.Combine(folder, "model.onnx");
                    config.Model.Kokoro.Voices = Path.Combine(folder, "voices.bin");
                    config.Model.Kokoro.Tokens = Path.Combine(folder, "tokens.txt");
                    config.Model.Kokoro.DataDir = Path.Combine(folder, "espeak-ng-data");
                    config.Model.Kokoro.Lexicon = Path.Combine(folder, "lexicon-us-en.txt");
                    config.Model.Kokoro.Lang = "en-us";
                    config.MaxNumSentences = 1;
                    tts = new OfflineTts(config);
                }
                OfflineTtsGeneratedAudio audio = tts.Generate(text, 1.1f, HerOwn);
                float[] samples = audio.Samples;
                short[] pcm = new short[samples.Length];
                for (int i = 0; i < samples.Length; i++) pcm[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(samples[i] * 32767)));
                return Voice.Wave(pcm, audio.SampleRate);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (tts != null) tts.Dispose();
                tts = null;
            }
        }
    }

    // Her own brain with no Ollama: the same llama.cpp build and models as Windows, kept in her folder and started with her.
    internal sealed class OwnBrain : IDisposable
    {
        private const string Build = "b11669";
        private const string Site = "https://github.com/ggml-org/llama.cpp/releases/download/" + Build + "/llama-" + Build + "-bin-";
        private readonly string root;
        private Process server;
        internal int Port;

        internal OwnBrain(string data) { root = Path.Combine(data, "brain"); }

        // File, checksum and size of the build for this computer.
        internal static string[] Package(bool mac, System.Runtime.InteropServices.Architecture chip)
        {
            if (!mac) return new[] { "ubuntu-vulkan-x64.tar.gz", "e8709e5ba6c1b093dea7f3de57f4aba842f9a188fba6b465782d65d6fcfa4d9f", "30497889" };
            return chip == System.Runtime.InteropServices.Architecture.Arm64
                ? new[] { "macos-arm64.tar.gz", "dbed55493d72bc1ba2babc8770d669c2289b5bc4ee9fb2a4dfc158d45b7d13fd", "12191582" }
                : new[] { "macos-x64.tar.gz", "cab28238d78158109b48deb27b05d50e0dcef56275d2e76da9ca8281526c7532", "11677404" };
        }

        // By memory: the graphics card's isn't asked here, so anything under 7 GB gets her smallest.
        internal static Pick Fit(long ram) { return Engine.Fit(new Machine { Ram = ram, Vram = 0 }); }

        private string Folder { get { return Path.Combine(root, "engine-" + Build + "-unix"); } }
        private string ModelPath(Pick pick) { return Path.Combine(root, "models", pick.File); }

        private string ServerFile
        {
            get
            {
                if (!Directory.Exists(Folder)) return null;
                string[] found = Directory.GetFiles(Folder, "llama-server", SearchOption.AllDirectories);
                return found.Length == 0 ? null : found[0];
            }
        }

        internal Pick Installed()
        {
            if (ServerFile == null) return null;
            foreach (Pick pick in Engine.Picks) if (File.Exists(ModelPath(pick))) return pick;
            return null;
        }

        internal void Install(Pick pick, Action<string, long, long> progress)
        {
            InstallEngine(progress);
            if (File.Exists(ModelPath(pick))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath(pick)));
            Engine.Fetch(pick.Url, ModelPath(pick), pick.Size, pick.Sha256, pick.Name, progress, delegate { return false; });
        }

        internal void InstallEngine(Action<string, long, long> progress)
        {
            Directory.CreateDirectory(root);
            if (ServerFile == null)
            {
                string[] package = Package(OperatingSystem.IsMacOS(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
                string archive = Path.Combine(root, "engine-" + Build + ".tar.gz"), staging = Folder + ".new";
                Engine.Fetch(Site + package[0], archive, long.Parse(package[2]), package[1], "the engine", progress, delegate { return false; });
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                string problem = Music.RunFor("tar", new[] { "-xzf", archive, "-C", staging }, 120000);
                if (Directory.GetFiles(staging, "llama-server", SearchOption.AllDirectories).Length == 0) throw new IOException("The engine download is missing llama-server." + (string.IsNullOrEmpty(problem) ? "" : " " + Voice.Short(problem)));
                if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
                Directory.Move(staging, Folder);
                File.Delete(archive);
            }
        }

        internal string Version()
        {
            string file = ServerFile;
            if (file == null) return null;
            // It says its version on stderr.
            ProcessStartInfo start = new ProcessStartInfo(file, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (Process running = Process.Start(start))
            {
                running.OutputDataReceived += delegate { };
                running.BeginOutputReadLine();
                string said = running.StandardError.ReadToEnd();
                running.WaitForExit(10000);
                return said;
            }
        }

        // Starts it and waits until it answers; the address, or an exception saying why not.
        internal string Start(Pick pick, string modelOverride = null)
        {
            string file = ServerFile;
            if (file == null) throw new IOException("Her own brain isn't installed.");
            Port = Engine.FreePort();
            ProcessStartInfo start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(file) };
            foreach (string word in new[] { "-m", modelOverride ?? ModelPath(pick), "--host", "127.0.0.1", "--port", Port.ToString(), "-c", "4096", "-ngl", "99", "--jinja" }) start.ArgumentList.Add(word);
            start.Environment["LD_LIBRARY_PATH"] = Path.GetDirectoryName(file);
            server = Process.Start(start);
            server.OutputDataReceived += delegate { };
            server.ErrorDataReceived += delegate { };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            string address = "http://127.0.0.1:" + Port;
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                for (int tries = 0; tries < 240; tries++)
                {
                    if (server.HasExited) throw new IOException("Her own brain stopped while it was loading.");
                    try { if (client.GetAsync(address + "/health").GetAwaiter().GetResult().IsSuccessStatusCode) return address; }
                    catch (Exception) { }
                    Thread.Sleep(500);
                }
            throw new IOException("Her own brain took too long to load.");
        }

        public void Dispose()
        {
            try { if (server != null && !server.HasExited) server.Kill(true); }
            catch (Exception) { }
            server = null;
        }
    }

    internal sealed partial class Pet
    {
        private FreeSpeaker freeVoice;
        private OwnBrain ownBrain;
        private bool ownBrainBusy, freeVoiceBusy;

        internal FreeSpeaker FreeVoice { get { return freeVoice ?? (freeVoice = new FreeSpeaker(Program.Smoke ?? folder)); } }
        internal OwnBrain OwnBrainHere { get { return ownBrain ?? (ownBrain = new OwnBrain(Program.Smoke ?? folder)); } }

        internal void GetFreeVoice()
        {
            if (FreeVoice.Installed || freeVoiceBusy) return;
            freeVoiceBusy = true;
            Talk.Answer("(voice)", "Getting my free voice: " + Engine.Amount(VoicePack.Size) + ", one time. I'll say when it's in.");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null;
                try { FreeVoice.Install(delegate { }); }
                catch (Exception error) { problem = error.Message; }
                Dispatcher.UIThread.Post(delegate
                {
                    freeVoiceBusy = false;
                    if (problem != null) { Brain.Log("free voice", -1, problem); Talk.Answer("(voice)", "My free voice didn't download: " + Voice.Short(problem)); return; }
                    Reply(++asking, "(voice)", "There. This is my free voice, choom.");
                });
            });
        }

        // Brain > Her own brain: downloads once, then starts with her.
        internal void GetOwnBrain()
        {
            if (ownBrainBusy) return;
            ownBrainBusy = true;
            Pick pick = OwnBrainHere.Installed() ?? OwnBrain.Fit(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            if (OwnBrainHere.Installed() == null) Talk.Answer("(brain server)", "Getting " + pick.Name + ": " + Engine.Amount(pick.Size) + ", one time. It runs right here on this computer.");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string address = null, problem = null;
                try
                {
                    OwnBrainHere.Install(pick, delegate { });
                    address = OwnBrainHere.Start(pick);
                }
                catch (Exception error) { problem = error.Message; }
                Dispatcher.UIThread.Post(delegate
                {
                    ownBrainBusy = false;
                    if (problem != null) { Brain.Log("own brain", -1, problem); Talk.Answer("(brain server)", "My own brain didn't start: " + Voice.Short(problem)); return; }
                    Saved.OwnBrain = true;
                    UseServer(address);
                });
            });
        }
    }
}
