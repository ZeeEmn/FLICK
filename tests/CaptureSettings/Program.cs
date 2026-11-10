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
            var preferences=Activator.CreateInstance(Type("ProductSettings"),true)!;
            bool editable=false;int applications=0;
            Func<Task> apply=()=>{applications++;return Task.CompletedTask;};
            StackPanel Create()=>(StackPanel)Activator.CreateInstance(Type("QualityControls"),I,null,new object[]{preferences,(Func<bool>)(()=>editable),apply},null)!;
            var home=Create();var sidebar=Create();
            object Field(object obj,string name)=>obj.GetType().GetField(name,I)!.GetValue(obj)!;
            void Refresh(object obj)=>obj.GetType().GetMethod("Refresh",I)!.Invoke(obj,null);
            var profile=Type("ProductSettings").GetProperty("Profile")!;
            var rate=Type("ProductSettings").GetProperty("FrameRate")!;
            foreach(var view in new[]{home,sidebar}) {
                var selector=(ComboBox)Field(view,"selector");
                var buttons=(Dictionary<int,Button>)Field(view,"fpsChoices");
                Check(!selector.IsEnabled&&buttons.Values.All(b=>!b.IsEnabled),"Profile and FPS disabled while replay is armed");
                selector.SelectedValue="High";buttons[30].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)profile.GetValue(preferences)! =="Default"&&(int)rate.GetValue(preferences)! ==60&&applications==0,"Delayed profile/FPS events cannot mutate active settings");
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
                Set("armed",false);Check(Allowed(),"Fully paused capture settings unlock");
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
