using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Xfer.Lab;

internal static partial class CameraLab
{
    // Exercises the production camera adapter from a worker thread, without a receiver window or UI automation.
    private static int FocusHoldProbe(string[] args)
    {
        if (args.Length < 2 || args.Length > 3) throw new ArgumentException("focus-hold OUT [CAMERA]");
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Output directory must be empty: " + output);
        var selected = SelectCamera(args.Length == 3 ? args[2] : "C920");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("Xfer_LOG_DIR", Path.Combine(output, "trace"));
        using var log = Writer(output, "controls.jsonl");
        var clock = Stopwatch.StartNew();
        var camera = new MediaFoundationCamera(selected.Id, 1920, 30);
        IFocusHold focus = camera;
        using var first = new ManualResetEventSlim();
        CameraControlState original = default, shutdown = default;
        int frames = 0;
        bool released = false, passed = false;
        string failure = null;
        CameraControlState Snapshot(string stage)
        {
            var state = camera.ReadControl(MediaFoundationCamera.CameraControlFocus, true);
            log.WriteLine(JsonSerializer.Serialize(new
            {
                Stage = stage, AtMs = clock.Elapsed.TotalMilliseconds, Frames = Volatile.Read(ref frames),
                Focus = state, Exposure = camera.ReadControl(MediaFoundationCamera.CameraControlExposure, true),
                Gain = camera.ReadControl(MediaFoundationCamera.VideoProcAmpGain, false)
            }, Json));
            log.Flush();
            return state;
        }
        camera.BeforeShutdown += () =>
        {
            shutdown = Snapshot("shutdown_before_lab_restore");
            if (original.Supported)
                camera.SetControl(true, MediaFoundationCamera.CameraControlFocus, original.Automatic ? null : original.Value, "focus");
            Snapshot("shutdown_after_lab_restore");
        };
        try
        {
            camera.Start((_, _, _) => { Interlocked.Increment(ref frames); first.Set(); });
            Require(first.Wait(15000), "camera produces a frame: " + camera.Error);
            original = Snapshot("initial");
            Require(original.Supported && original.Automatic, "probe requires autofocus initially on");
            Thread.Sleep(1000);
            Task.Run(focus.HoldFocus).GetAwaiter().GetResult();
            var held = Snapshot("held");
            Require(held.Supported && !held.Automatic, "worker holds focus");
            Thread.Sleep(1000);
            Task.Run(focus.HoldFocus).GetAwaiter().GetResult();
            var stable = Snapshot("repeated_hold");
            Require(stable.Supported && !stable.Automatic && stable.Value == held.Value, "hold stays fixed");
            Task.Run(focus.ReleaseFocus).GetAwaiter().GetResult();
            Require(Snapshot("released").Automatic, "worker restores autofocus");
            Task.Run(focus.ReleaseFocus).GetAwaiter().GetResult();
            Require(Snapshot("repeated_release").Automatic, "repeated release is harmless");
            Require(camera.SetControl(true, MediaFoundationCamera.CameraControlFocus, held.Value, "focus"), "set explicit manual focus");
            Task.Run(() => { focus.HoldFocus(); focus.ReleaseFocus(); }).GetAwaiter().GetResult();
            var manual = Snapshot("manual_preserved");
            Require(manual.Supported && !manual.Automatic && manual.Value == held.Value, "existing manual focus is preserved");
            Require(camera.SetControl(true, MediaFoundationCamera.CameraControlFocus, null, "focus"), "restore AF for shutdown test");
            Thread.Sleep(500);
            Task.Run(focus.HoldFocus).GetAwaiter().GetResult();
            Require(!Snapshot("held_before_dispose").Automatic, "held before dispose");
            camera.Dispose();
            released = camera.WaitForStop(10000);
            Require(released && shutdown.Supported && shutdown.Automatic, "dispose restored AF before lab hook and released camera");
            passed = true;
        }
        catch (Exception e) { failure = e.Message; }
        finally
        {
            camera.Dispose();
            released = camera.WaitForStop(10000);
            Diagnostics.Flush();
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                Passed = passed && released, Error = failure, DeviceReleased = released, Frames = frames,
                Original = original, ShutdownBeforeLabRestore = shutdown, Seconds = clock.Elapsed.TotalSeconds,
                Scope = "IFocusHold adapter only; no receiver policy, QR/FX2 decode or UI test"
            }, Json));
        }
        Console.WriteLine(File.ReadAllText(Path.Combine(output, "summary.json")));
        return passed && released ? 0 : 3;
    }
}
