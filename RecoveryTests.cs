using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TopViewport;

/// <summary>Deterministic policy/file tests only; never restore a real display or simulate real system sleep.</summary>
internal static class RecoveryTests
{
    private const long Second = TimeSpan.TicksPerSecond;
    public static IReadOnlyList<string> Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var passed = new List<string>();
        var utc = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        const long start = 100 * Second;
        SessionState Active(bool confirmed = true) => new()
        {
            SessionId = "recovery-test-session", Confirmed = confirmed, Heartbeat = utc,
            HeartbeatAwakeTicks = start, ConfirmationDeadline = utc.AddSeconds(20),
            ConfirmationDeadlineAwakeTicks = start + 20 * Second
        };
        void Expect(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Recovery test failed: " + name);
            passed.Add(name);
        }

        {
            var state = Active(); var policy = new WatchdogPolicy(state.SessionId);
            Expect(policy.Evaluate(state, true, start, utc) == WatchdogDecision.Continue, "healthy parent continues");
            for(int second=1;second<=12;second++) policy.Evaluate(state,true,start+second*Second,utc.AddSeconds(second));
            Expect(policy.Evaluate(state, true, start + 13 * Second, utc.AddSeconds(13)) == WatchdogDecision.Recover && policy.Reason == "heartbeat-stalled", "awake UI stall still recovers after 12 seconds");
        }
        {
            var state = Active(); var policy = new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state, true, start, utc);
            Expect(policy.Evaluate(state, true, start + Second, utc.AddHours(8)) == WatchdogDecision.Continue && policy.Reason == "resume-grace", "eight hours asleep do not age awake heartbeat");
            for(int second=2;second<=28;second++) policy.Evaluate(state,true,start+second*Second,utc.AddHours(8).AddSeconds(second-1));
            Expect(policy.Evaluate(state, true, start + 29 * Second, utc.AddHours(8).AddSeconds(28)) == WatchdogDecision.Continue, "missed suspend broadcast grants 30 second resume grace");
            Expect(policy.Evaluate(state, true, start + 32 * Second, utc.AddHours(8).AddSeconds(31)) == WatchdogDecision.Recover, "resume grace expires if UI remains hung");
        }
        {
            var state = Active(false); var policy = new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state, true, start, utc);
            Expect(policy.Evaluate(state, true, start + Second, utc.AddDays(1)) == WatchdogDecision.Continue, "unconfirmed session survives sleep past UTC confirmation deadline");
            state.Heartbeat = utc.AddDays(1).AddSeconds(1); state.HeartbeatAwakeTicks = start + 2 * Second;
            state.ConfirmationDeadline = utc.AddDays(1).AddSeconds(21); state.ConfirmationDeadlineAwakeTicks = start + 22 * Second;
            state.ResumeGeneration = 1; state.ResumeGraceUntilAwakeTicks = start + 32 * Second;
            Expect(policy.Evaluate(state, true, start + 2 * Second, utc.AddDays(1).AddSeconds(1)) == WatchdogDecision.Continue, "GUI resume extends unconfirmed session");
            for(int second=3;second<=33;second++) policy.Evaluate(state,true,start+second*Second,utc.AddDays(1).AddSeconds(second-1));
            Expect(policy.Evaluate(state, true, start + 34 * Second, utc.AddDays(1).AddSeconds(33)) == WatchdogDecision.Recover && policy.Reason == "confirmation-expired", "unconfirmed timeout still works after resume grace");
        }
        {
            var state = Active(); state.Suspended = true;
            var policy = new WatchdogPolicy(state.SessionId);
            Expect(policy.Evaluate(state, true, start + 1000 * Second, utc.AddHours(2)) == WatchdogDecision.Continue, "explicit suspend ignores timeouts while parent lives");
            Expect(policy.Evaluate(state, false, start + 1001 * Second, utc.AddHours(2).AddSeconds(1)) == WatchdogDecision.Recover && policy.Reason == "parent-exited", "dead parent recovers even when suspended");
        }
        {
            var state=Active(); state.Suspended=true;
            var policy=new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state,true,start,utc);
            bool stayedSuspended=true;
            for(int second=1;second<=60;second++)
                stayedSuspended &= policy.Evaluate(state,true,start+second*Second,utc.AddSeconds(second))==WatchdogDecision.Continue;
            Expect(stayedSuspended,"suspended UI receives a full 60 awake seconds before stale-heartbeat fallback");
            Expect(policy.Evaluate(state,true,start+61*Second,utc.AddSeconds(61))==WatchdogDecision.Recover && policy.Reason=="suspended-parent-stalled",
                "UI hung in suspended state still recovers after 60 awake seconds");
        }
        {
            var state=Active(); state.Suspended=true;
            var policy=new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state,true,start,utc);
            Expect(policy.Evaluate(state,true,start+Second,utc.AddDays(10))==WatchdogDecision.Continue,
                "ten days actually asleep do not consume suspended awake timeout");
            for(int second=2;second<=61;second++)
            {
                state.HeartbeatAwakeTicks=start+second*Second; state.Heartbeat=utc.AddDays(10).AddSeconds(second-1);
                policy.Evaluate(state,true,start+second*Second,state.Heartbeat);
            }
            Expect(policy.Evaluate(state,true,start+62*Second,utc.AddDays(10).AddSeconds(61))==WatchdogDecision.Continue,
                "suspended marker with a responsive UI heartbeat does not force recovery");
        }
        {
            var state=Active(); var policy=new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state,true,start,utc);
            Expect(policy.Evaluate(state,true,start+60*Second,utc.AddSeconds(60))==WatchdogDecision.Continue && policy.Reason=="resume-grace",
                "watchdog scheduling pause receives resume grace even without a wall-clock gap");
            for(int second=61;second<=90;second++) policy.Evaluate(state,true,start+second*Second,utc.AddSeconds(second));
            Expect(policy.Evaluate(state,true,start+91*Second,utc.AddSeconds(91))==WatchdogDecision.Recover,
                "watchdog scheduling-pause grace remains bounded");
        }
        {
            var state = Active(); state.ResumeGraceUntilAwakeTicks = start + 100 * Second;
            var policy = new WatchdogPolicy(state.SessionId);
            Expect(policy.Evaluate(state, false, start, utc) == WatchdogDecision.Recover, "dead parent overrides resume grace");
        }
        {
            var state = Active(); var policy = new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state, true, start, utc);
            state.SessionId = "replacement-session";
            Expect(policy.Evaluate(state, false, start + 100 * Second, utc.AddSeconds(100)) == WatchdogDecision.Exit, "old watchdog exits on session replacement before checking parent or stale clocks");
            Expect(new WatchdogPolicy("old-session").Evaluate(state, false, start, utc) == WatchdogDecision.Exit, "delayed old watchdog never adopts replacement on its first read");
        }
        {
            var state = Active(); var policy = new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state, true, start, utc);
            Expect(policy.Evaluate(state, true, start + Second, utc.AddHours(-1)) == WatchdogDecision.Continue, "wall clock moves backwards without expiring a session");
        }
        {
            var state = Active(false); var policy = new WatchdogPolicy(state.SessionId);
            policy.Evaluate(state, true, start, utc);
            for(int second=1;second<=20;second++) policy.Evaluate(state,true,start+second*Second,utc.AddSeconds(second));
            Expect(policy.Evaluate(state, true, start + 21 * Second, utc.AddSeconds(21)) == WatchdogDecision.Recover && policy.Reason == "confirmation-expired", "ordinary unconfirmed timeout remains enforced");
        }
        {
            var state = Active(false);
            Recovery.MarkSuspended(state);
            Expect(state.Suspended && Recovery.IsResumeGrace(state), "GUI suspend marker suppresses confirmation timeout");
            Recovery.MarkResumed(state);
            Expect(!state.Suspended && state.ResumeGeneration == 1 && Recovery.IsResumeGrace(state) &&
                Recovery.ConfirmationSecondsRemaining(state) > 18 && !Recovery.IsConfirmationExpired(state),
                "GUI resume helper refreshes heartbeat, confirmation timer and rebuild grace together");
        }
        {
            string file = Path.Combine(folder, "session-isolation-" + Guid.NewGuid().ToString("N") + ".json");
            var current = Active(); current.SessionId = Guid.NewGuid().ToString("N"); current.Completed = true;
            Recovery.Write(current, file);
            var newer = Active(); newer.SessionId = Guid.NewGuid().ToString("N");
            Recovery.BeginSession(newer, file);
            string before = File.ReadAllText(file);
            current.Completed = false;
            Recovery.Write(current, file);
            Expect(current.Completed && File.ReadAllText(file) == before, "late old heartbeat cannot overwrite new session JSON");
            // This must return before ClipCursor or any real display-restore API.
            current.Completed = false; current.SnapshotPath = "must-not-load-this-file";
            Expect(Recovery.Restore(current, file) && current.Completed && File.ReadAllText(file) == before, "obsolete recovery exits without restoring topology or writing replacement state");
            Expect(Recovery.Read(file)!.HeartbeatAwakeTicks > 0 && Recovery.Read(file)!.ConfirmationDeadlineAwakeTicks > 0, "session persistence records sleep-excluding heartbeat and confirmation clocks");
            var third = Active(); third.SessionId = Guid.NewGuid().ToString("N");
            bool rejected = false;
            try { Recovery.BeginSession(third, file); } catch (InvalidOperationException) { rejected = true; }
            Expect(rejected && File.ReadAllText(file) == before, "new session cannot replace incomplete recovery state");
            newer.Completed = true; Recovery.Write(newer, file);
            newer.Completed = false; Recovery.Write(newer, file);
            Expect(newer.Completed && Recovery.Read(file)!.Completed, "late same-session heartbeat cannot undo completed recovery");
        }
        {
            string file=Path.Combine(folder,"reader-sharing-"+Guid.NewGuid().ToString("N")+".json");
            var state=Active(); state.SessionId=Guid.NewGuid().ToString("N");
            Recovery.Write(state,file);
            using(var openReader=Recovery.OpenStateReadStream(file))
            {
                state.ResumeGeneration=1;
                Recovery.Write(state,file);
                var old=JsonSerializer.Deserialize<SessionState>(openReader);
                Expect(old?.ResumeGeneration==0 && Recovery.Read(file)?.ResumeGeneration==1,
                    "state reader permits atomic replacement while preserving its original complete JSON snapshot");
            }
            using var stop=new CancellationTokenSource();
            using var ready=new ManualResetEventSlim();
            long reads=0,errors=0,nullReads=0;
            var reader=Task.Run(()=>
            {
                ready.Set();
                while(!stop.IsCancellationRequested)
                {
                    var observed=Recovery.Read(file);
                    if(observed is null) Interlocked.Increment(ref nullReads);
                    else if(observed.SessionId!=state.SessionId) Interlocked.Increment(ref errors);
                    Interlocked.Increment(ref reads);
                }
            });
            try
            {
                if(!ready.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Concurrent reader did not start");
                for(int iteration=2;iteration<=82;iteration++) { state.ResumeGeneration=iteration; Recovery.Write(state,file); }
            }
            finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
            if(!(reads>0 && errors==0 && nullReads==0 && Recovery.Read(file)?.ResumeGeneration==82))
                throw new InvalidOperationException($"Concurrent state test: reads={reads}, nullReads={nullReads}, wrongSessions={errors}, finalGeneration={Recovery.Read(file)?.ResumeGeneration}");
            Expect(true,
                "concurrent watchdog-style Read loops and 81 atomic heartbeat writes have no sharing errors or partial JSON");
            var blockingReader=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var release=Task.Run(()=> { Thread.Sleep(45); blockingReader.Dispose(); });
            try { state.ResumeGeneration=83; Recovery.Write(state,file); }
            finally { blockingReader.Dispose(); release.GetAwaiter().GetResult(); }
            Expect(Recovery.Read(file)?.ResumeGeneration==83,"atomic state replacement retries a short legacy reader delete-sharing denial");
            using(var persistentBlock=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            {
                bool rejected=false;
                var elapsed=Stopwatch.StartNew();
                try { state.ResumeGeneration=84; Recovery.Write(state,file); }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException) { rejected=true; }
                Expect(rejected && elapsed.ElapsedMilliseconds<600 && Recovery.Read(file)?.ResumeGeneration==83,
                    "persistent state replacement denial remains bounded and preserves the previous valid state");
            }
        }
        {
            string file=Path.Combine(folder,"apply-serialization-"+Guid.NewGuid().ToString("N")+".json");
            var state=Active(); state.SessionId=Guid.NewGuid().ToString("N");
            Recovery.Write(state,file);
            int calls=0;
            var result=new DisplayApplyResult("test-virtual","test-physical",new DisplayMode(1920,972,165,32),
                new DisplayRectangle(0,0,1920,972),new DisplayRectangle(0,972,1920,2052),0);
            DisplayApplyResult MockApply() { calls++; return result; }
            Expect(ReferenceEquals(Recovery.ApplyCurrentSessionAtPath(state,file,MockApply),result) && calls==1,
                "active matching session may apply under the display-recovery mutex using a pure mock");
            state.Completed=true; Recovery.Write(state,file);
            bool blocked=false;
            try { Recovery.ApplyCurrentSessionAtPath(state,file,MockApply); } catch(InvalidOperationException) { blocked=true; }
            Expect(blocked && calls==1,"completed recovery prevents a later apply callback from re-enabling the old layout");
            var replacement=Active(); replacement.SessionId=Guid.NewGuid().ToString("N");
            Recovery.BeginSession(replacement,file);
            state.Completed=false; blocked=false;
            try { Recovery.ApplyCurrentSessionAtPath(state,file,MockApply); } catch(InvalidOperationException) { blocked=true; }
            Expect(blocked && calls==1,"replaced session never invokes its stale apply callback");
            using var held=new ManualResetEventSlim();
            using var release=new ManualResetEventSlim();
            var holder=Task.Run(()=>Recovery.ApplyCurrentSessionAtPath(replacement,file,()=>
            { held.Set(); if(!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Test apply release timeout"); return result; }));
            try
            {
                if(!held.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Mock apply did not acquire recovery mutex");
                blocked=false;
                var elapsed=Stopwatch.StartNew();
                try { Recovery.ApplyCurrentSessionAtPath(replacement,file,MockApply); } catch(TimeoutException) { blocked=true; }
                Expect(blocked && calls==1 && elapsed.ElapsedMilliseconds<1100,
                    "competing display apply cannot enter during another apply and times out near 500 ms");
            }
            finally { release.Set(); holder.GetAwaiter().GetResult(); }
        }
        {
            string file=Path.Combine(folder,"unreadable-session-"+Guid.NewGuid().ToString("N")+".json");
            var state=Active(); state.SessionId=Guid.NewGuid().ToString("N");
            const string damaged="{damaged-recovery-json";
            File.WriteAllText(file,damaged);
            bool blocked=false;
            try { Recovery.Write(state,file); } catch(IOException) { blocked=true; }
            Expect(blocked && File.ReadAllText(file)==damaged,"existing malformed state cannot be silently overwritten as if no session existed");
            state.SnapshotPath="must-not-load-an-unidentified-session";
            Expect(!Recovery.Restore(state,file) && !state.Completed && File.ReadAllText(file)==damaged,
                "existing unreadable state prevents recovery before touching cursor or display topology");
            File.Delete(file);
            Recovery.Write(state,file);
            string original=File.ReadAllText(file);
            using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                blocked=false;
                try { state.ResumeGeneration=1; Recovery.Write(state,file); } catch(IOException) { blocked=true; }
                Expect(blocked,"exclusive external state reader prevents an unsafe blind heartbeat overwrite");
            }
            Expect(File.ReadAllText(file)==original,"state blocked by an external exclusive handle remains byte-for-byte unchanged");
        }
        File.WriteAllText(Path.Combine(folder, "recovery-policy-results.json"), JsonSerializer.Serialize(new { Passed = passed.Count, Failed = 0, Checks = passed }, Program.JsonOptions));
        return passed;
    }
}
