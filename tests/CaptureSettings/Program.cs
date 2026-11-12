using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;

static class Program
{
    const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string folder=Path.GetFullPath(args[0]);
            string assets=Path.Combine(AppContext.BaseDirectory,"Assets");Directory.CreateDirectory(assets);
            foreach(string name in new[]{"flick.ico","flick.png"})File.Copy(Path.Combine(folder,"Assets",name),Path.Combine(assets,name),true);
            AssemblyLoadContext.Default.Resolving+=(_,name)=>File.Exists(Path.Combine(folder,name.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder,name.Name+".dll")):null;
            var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder,"FLICK.dll"));
            Type Type(string name)=>assembly.GetType("Rewind."+name,true)!;
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var planMemory=Type("MemoryPolicy").GetMethod("Plan",BindingFlags.Static|BindingFlags.NonPublic)!;
            object Memory(long available,string profile,int requested=300)=>planMemory.Invoke(null,[requested,profile,available])!;
            int Seconds(object plan)=>(int)plan.GetType().GetProperty("Seconds")!.GetValue(plan)!;
            long VideoBytes(object plan)=>(long)plan.GetType().GetProperty("VideoBytes")!.GetValue(plan)!;
            const long GiB=1024L*1024*1024;
            var highMemory=Memory(19*GiB,"Very High");
            Check(Seconds(highMemory)==300&&VideoBytes(highMemory)>310L*7_500_000,
                "A 32 GB PC with 19 GiB available can request five minutes at Very High");
            var lowMemory=Memory(2*GiB,"Very High");
            Check(Seconds(lowMemory)>0&&Seconds(lowMemory)<300&&Seconds(Memory(2*GiB,"Default"))>Seconds(lowMemory),
                "Low available RAM produces a shorter duration and lower bitrate allows more history");
            Check(Seconds(Memory(256*1024*1024,"Very High"))==0,"Very low available RAM refuses unsafe replay allocation");
            var preferences=Activator.CreateInstance(Type("ProductSettings"),true)!;
            bool editable=false;int applications=0;
            Func<Task> apply=()=>{applications++;return Task.CompletedTask;};
            StackPanel Create()=>(StackPanel)Activator.CreateInstance(Type("QualityControls"),I,null,new object[]{preferences,(Func<bool>)(()=>editable),apply},null)!;
            var home=Create();var sidebar=Create();
            object Field(object obj,string name)=>obj.GetType().GetField(name,I)!.GetValue(obj)!;
            void Refresh(object obj)=>obj.GetType().GetMethod("Refresh",I)!.Invoke(obj,null);
            var profile=Type("ProductSettings").GetProperty("Profile")!;
            var rate=Type("ProductSettings").GetProperty("FrameRate")!;
            profile.SetValue(preferences,"Default");
            Type("ProductSettings").GetMethod("Normalize",I)!.Invoke(preferences,null);
            Check((string)profile.GetValue(preferences)! =="Balanced","Saved Default profile migrates to Balanced");
            Refresh(home);Refresh(sidebar);
            foreach(var view in new[]{home,sidebar}) {
                var selector=(ComboBox)Field(view,"selector");
                var buttons=(Dictionary<int,Button>)Field(view,"fpsChoices");
                Check(!selector.IsEnabled&&buttons.Values.All(b=>!b.IsEnabled),"Profile and FPS disabled while replay is armed");
                selector.SelectedValue="High";buttons[30].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)profile.GetValue(preferences)! =="Balanced"&&(int)rate.GetValue(preferences)! ==60&&applications==0,"Delayed profile/FPS events cannot mutate active settings");
            }
            editable=true;Refresh(home);Refresh(sidebar);
            ((ComboBox)Field(home,"selector")).SelectedValue="High";
            ((Dictionary<int,Button>)Field(sidebar,"fpsChoices"))[30].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Refresh(home);Refresh(sidebar);
            Check((string)profile.GetValue(preferences)! =="High"&&(int)rate.GetValue(preferences)! ==30&&applications==2,"Paused profile and FPS edits apply to the shared settings model");
            Check(((ComboBox)Field(sidebar,"selector")).SelectedValue?.ToString()=="High","Home and sidebar agree after refresh");
            editable=false;Refresh(home);Refresh(sidebar);
            Check(!((ComboBox)Field(home,"selector")).IsEnabled&&!((ComboBox)Field(sidebar,"selector")).IsEnabled,"Both views relock when replay resumes");
            using var engine=(IDisposable)Activator.CreateInstance(Type("ReplayEngine"),true)!;
            var shell=(Window)Activator.CreateInstance(Type("Shell"),engine,(Action)(()=>{}),true)!;
            void Set(string name,bool value)=>Type("Shell").GetField(name,I)!.SetValue(shell,value);
            bool Allowed()=>(bool)Type("Shell").GetProperty("CanChangeCaptureSettings",I)!.GetValue(shell)!;
            try {
                Check(!Allowed(),"Startup remains locked before engine becomes active");
                var settingsView=(Grid)Type("Shell").GetMethod("SettingsView",I)!.Invoke(shell,[true])!;
                var backend=((StackPanel)settingsView.Children[1]).Children.OfType<ComboBox>()
                    .Single(box=>box.Items.Contains("Windows Graphics Capture (Beta)"));
                Check(!backend.IsEnabled&&backend.SelectedIndex==0,"DXGI is the default and the backend is locked while replay is armed");
                backend.SelectedIndex=1;
                Check(backend.SelectedIndex==0,"A delayed selection cannot switch capture backends while armed");
                Set("armed",false);Check(Allowed(),"Fully paused capture settings unlock");
                Type("Shell").GetMethod("UpdateStates",I)!.Invoke(shell,null);
                Check(backend.IsEnabled,"Backend selection is enabled after pausing");
                backend.SelectedIndex=1;
                var shellPreferences=Type("Shell").GetProperty("Preferences",I)!.GetValue(shell)!;
                var autoResume=Type("ProductSettings").GetProperty("AutoResumeReplay")!;
                var timer=(System.Windows.Threading.DispatcherTimer)Field(shell,"autoResumeTimer");
                var updateTimer=Type("Shell").GetMethod("UpdateAutoResumeTimer",I)!;
                updateTimer.Invoke(shell,null);
                Check(!(bool)autoResume.GetValue(shellPreferences)!&&!timer.IsEnabled,"Auto-resume defaults to off");
                autoResume.SetValue(shellPreferences,true);
                updateTimer.Invoke(shell,null);
                Check(timer.IsEnabled&&timer.Interval==TimeSpan.FromMinutes(2),"Opting in while paused schedules a two-minute resume");
                autoResume.SetValue(shellPreferences,false);
                updateTimer.Invoke(shell,null);
                Check(!timer.IsEnabled,"Turning auto-resume off cancels the pending resume");
                string notes;
                using(var resource=assembly.GetManifestResourceStream("Rewind.PatchNotes.txt")!)
                using(var reader=new StreamReader(resource))notes=reader.ReadToEnd();
                var noteContent=(StackPanel)Type("PatchNotes").GetMethod("FormatNotes",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[notes])!;
                var panels=noteContent.Children.OfType<Border>().ToArray();
                Check(panels.Length==3,"Patch notes use one panel per category");
                var featured=(StackPanel)panels.Single(panel=>panel.BorderThickness.Left==3).Child;
                Check(featured.Children.OfType<TextBlock>().Any(text=>text.Text=="Windows Graphics Capture (Beta)")&&
                    featured.Children.OfType<Expander>().Single().IsExpanded,"New highlights the WGC announcement and opens its developer notes");
                var secondary=panels.Where(panel=>panel.BorderThickness.Left!=3).Select(panel=>(StackPanel)panel.Child).ToArray();
                Check(secondary.SelectMany(panel=>panel.Children.OfType<TextBlock>()).Any(text=>text.Text=="QOL Changes:")&&
                    secondary.SelectMany(panel=>panel.Children.OfType<Expander>()).Count()==11&&
                    secondary.SelectMany(panel=>panel.Children.OfType<Expander>()).All(note=>!note.IsExpanded),
                    "QOL and bug fix developer notes start collapsed");
                var version=(TextBlock)Type("Shell").GetMethod("VersionLabel",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
                Check((version.Text=="FLICK v1.2.0"||version.Text.StartsWith("FLICK v1.2.0-dev."))&&
                    !version.Text.Contains('+'),"Home footer uses the build version without commit metadata");
                Check((string)Type("ProductSettings").GetProperty("CaptureBackend")!.GetValue(shellPreferences)! == "Windows Graphics Capture (Beta)"&&Allowed(),
                    "Selecting WGC Beta while paused records the preference without starting replay");
                var duration=Type("ProductSettings").GetProperty("Seconds")!;
                duration.SetValue(shellPreferences,300);
                profile.SetValue(shellPreferences,"Very High");
                ((Task)Type("Shell").GetMethod("ApplyQualityAsync",I)!.Invoke(shell,null)!).GetAwaiter().GetResult();
                Check((int)duration.GetValue(shellPreferences)! == 300,"Changing profile preserves the selected replay length");
                Set("changing",true);Check(!Allowed(),"Stopping/starting transition remains locked");Set("changing",false);
                Type("ReplayEngine").GetProperty("Active",I)!.SetValue(engine,true);Check(!Allowed(),"Active engine cannot be edited even if pause was requested");
                Type("ReplayEngine").GetProperty("Active",I)!.SetValue(engine,false);
                Set("armed",true);Check(!Allowed(),"Automatic recovery remains locked");Set("armed",false);
                Type("Shell").GetMethod("ApplyQualityAsync",I)!.Invoke(shell,null);
                Check(!((Task)Type("Shell").GetMethod("ApplyQualityAsync",I)!.Invoke(shell,null)!).IsFaulted&&Allowed(),"Applying paused quality settings keeps replay paused");
            } finally {Type("Shell").GetMethod("Release",I)!.Invoke(shell,null);shell.Close();}
            Console.WriteLine("ALL CAPTURE SETTINGS REGRESSIONS PASSED");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
