using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

public sealed class ServerState {
 public bool Ready; public bool Oom; public int Players; public double Tps=double.NaN,Tick=double.NaN;
 public DateTime TpsTime=DateTime.MinValue; public DateTime LagTime=DateTime.MinValue;
 public readonly HashSet<string> Names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 public string Activity="Ожидание запуска";
 public void Parse(string line) {
  line=Regex.Replace(line,"\u001b\\[[0-9;]*m","");
  if(line.Contains("Done (") && line.Contains("For help")){Ready=true;Activity="Сервер готов. Можно подключаться.";}
  if(line.Contains("OutOfMemoryError")){Oom=true;Activity="Java сообщила о нехватке памяти.";}
  if(line.Contains("Can't keep up!"))LagTime=DateTime.UtcNow;
  var m=Regex.Match(line,@"There are (\d+) of a max of \d+ players online:\s*(.*)$");
  if(m.Success){Players=int.Parse(m.Groups[1].Value);Names.Clear();foreach(var n in m.Groups[2].Value.Split(',')){var s=n.Trim();if(s.Length>0)Names.Add(s);}}
  m=Regex.Match(line,@":\s*([A-Za-z0-9_]{1,16}) joined the game");
  if(m.Success){Names.Add(m.Groups[1].Value);Players=Names.Count;Activity=m.Groups[1].Value+" зашёл на сервер";}
  m=Regex.Match(line,@":\s*([A-Za-z0-9_]{1,16}) left the game");
  if(m.Success){Names.Remove(m.Groups[1].Value);Players=Names.Count;Activity=m.Groups[1].Value+" вышел с сервера";}
  m=Regex.Match(line,@"Overall:.*Mean tick time:\s*([\d.,]+).*Mean TPS:\s*([\d.,]+)",RegexOptions.IgnoreCase);
  if(m.Success){Tick=Number(m.Groups[1].Value);Tps=Number(m.Groups[2].Value);TpsTime=DateTime.UtcNow;}
  if(line.Contains("Saved the game")||line.Contains("Saved the world"))Activity="Мир сохранён";
 }
 public static double Number(string s){double v;return double.TryParse(s.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out v)?v:double.NaN;}
 public static double Heap(string s){var m=Regex.Match(s,@"garbage-first heap\s+total\s+\d+K,\s+used\s+(\d+)K",RegexOptions.IgnoreCase);return m.Success?Number(m.Groups[1].Value)/1048576.0:double.NaN;}
 public static string Advice(bool running,bool ready,bool oom,double heap,double max,double free,double tps,int pressure,int count,int limit,bool lag){
  if(oom)return "Java сообщила о нехватке памяти. После остановки увеличьте лимит на 2 ГБ, если на ПК есть запас. При повторении ошибки нужен журнал — причиной может быть мод или утечка.";
  if(free<2)return "На компьютере осталось меньше 2 ГБ свободной ОЗУ. Закройте лишние программы. Сейчас увеличивать память серверу не стоит.";
  if(!running)return "Всё готово к запуску. Первый старт сборки может занять несколько минут.";
  if(!ready)return "Загружаются моды и мир. Дождитесь статуса «Работает». Во время старта нагрузка может быть высокой.";
  if(pressure>=3){if(free>=4)return "Java heap заполнена более чем на 88% несколько замеров подряд. Можно выделить ещё 2 ГБ после остановки и проверить, стало ли лучше.";return "Java heap почти заполнена, но свободной памяти ПК мало. Закройте лишнее или уменьшите нагрузку на мир.";}
  if((!double.IsNaN(tps)&&tps<18)||lag)return "Сервер не успевает обрабатывать мир. Снизьте дальность симуляции, генерацию чанков или нагрузку механизмов. Дополнительная ОЗУ помогает только при нехватке памяти.";
  if(count>=limit-1 && count>0)return "Места почти закончились. При необходимости увеличьте максимум игроков в настройках после остановки. Это не увеличивает память автоматически.";
  return "Нагрузка без явных признаков нехватки памяти. Выделять больше ОЗУ только из-за числа игроков не требуется.";
 }
}

public sealed class PanelApp {
 static string capturePath;
 static bool integrationMode; static string integrationResult;
 [StructLayout(LayoutKind.Sequential)] class Mem {public uint Length=(uint)Marshal.SizeOf(typeof(Mem));public uint Load;public ulong Total,Avail,TotalPage,AvailPage,TotalVirtual,AvailVirtual,Extended;}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GlobalMemoryStatusEx([In,Out]Mem m);
 readonly string root; Window w; Process server; FileStream appLock; DispatcherTimer timer;
 readonly ConcurrentQueue<string> lines=new ConcurrentQueue<string>(); readonly ServerState state=new ServerState();
 readonly List<double> history=new List<double>(); double heap=double.NaN; DateTime heapTime=DateTime.MinValue;
 bool probing,stopping,closeAfter; int pressure,ticks,ram=8,maxPlayers=10; int launchedRam=8; DateTime started,lastSample,stopAt;
 TimeSpan lastCpu; string lastAdvice=""; string lastPlayers=""; int lastExit=-1;
 double totalMemory=32,availableMemory=16;
 const string ForgeArgs="libraries/net/minecraftforge/forge/1.20.1-47.4.13/win_args.txt";
 public PanelApp(string folder){root=folder;}
 T Find<T>(string n) where T:class{return w.FindName(n) as T;}
 void Text(string n,string value){Find<TextBlock>(n).Text=value;}
 void Message(string s){MessageBox.Show(w,s,"TFG · Сервер для друзей",MessageBoxButton.OK,MessageBoxImage.Information);}
 bool Running {get{try{return server!=null&&!server.HasExited;}catch{return false;}}}
 void ReadMemory(){var m=new Mem();if(GlobalMemoryStatusEx(m)){totalMemory=m.Total/1073741824.0;availableMemory=m.Avail/1073741824.0;}}
 string GetProp(string key,string fallback){var p=System.IO.Path.Combine(root,"server.properties");if(!File.Exists(p))return fallback;foreach(var l in File.ReadAllLines(p)){int i=l.IndexOf('=');if(i>0&&l.Substring(0,i).Trim()==key)return l.Substring(i+1).Trim();}return fallback;}
 int Port {get{int p;return int.TryParse(GetProp("server-port","25565"),out p)&&p>0&&p<65536?p:25565;}}
 void SetProp(string key,string value){var path=System.IO.Path.Combine(root,"server.properties");var ls=File.Exists(path)?File.ReadAllLines(path).ToList():new List<string>();ls.RemoveAll(l=>l.StartsWith(key+"=",StringComparison.Ordinal));ls.Add(key+"="+value);AtomicWrite(path,string.Join("\r\n",ls)+"\r\n");}
 static void AtomicWrite(string path,string text){var tmp=path+".panel-tmp";File.WriteAllText(tmp,text,new UTF8Encoding(false));if(File.Exists(path)){File.Replace(tmp,path,null);}else File.Move(tmp,path);}
 public void Run(){
  try{appLock=new FileStream(System.IO.Path.Combine(root,".tfg-panel.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch{MessageBox.Show("Панель для этой папки уже открыта.","TFG");return;}
  var app=new Application();using(var stream=typeof(PanelApp).Assembly.GetManifestResourceStream("Panel.xaml"))w=(Window)XamlReader.Load(stream);
  ReadMemory();var args=System.IO.Path.Combine(root,"user_jvm_args.txt");if(File.Exists(args)){var m=Regex.Match(File.ReadAllText(args),@"-Xmx(\d+)([GM])",RegexOptions.IgnoreCase);if(m.Success){int v=int.Parse(m.Groups[1].Value);ram=m.Groups[2].Value.Equals("M",StringComparison.OrdinalIgnoreCase)?Math.Max(4,v/1024):v;}}
  int.TryParse(GetProp("max-players","10"),out maxPlayers);if(maxPlayers<1)maxPlayers=10;
  var slider=Find<Slider>("RamSlider");slider.Maximum=Math.Max(4,Math.Min(32,Math.Floor(totalMemory-5)));slider.Value=Math.Max(4,Math.Min(slider.Maximum,ram));ram=(int)slider.Value;
  Find<TextBox>("MaxPlayersBox").Text=maxPlayers.ToString();slider.ValueChanged+=(s,e)=>UpdateRamHint();UpdateRamHint();
  Find<Button>("StartButton").Click+=(s,e)=>Start();Find<Button>("StopButton").Click+=(s,e)=>Stop();Find<Button>("SaveButton").Click+=(s,e)=>{Send("save-all flush");state.Activity="Запрошено сохранение мира…";};
  Find<Button>("ApplyButton").Click+=(s,e)=>SaveSettings();
  Find<Button>("OpenFolderButton").Click+=(s,e)=>OpenPath(root);
  Find<Button>("OpenLogButton").Click+=(s,e)=>{var p=System.IO.Path.Combine(root,"logs","latest.log");if(File.Exists(p))OpenPath(p);else Message("Журнал появится после первого запуска.");};
  Find<Button>("CopyAddressButton").Click+=(s,e)=>{var a=Find<ComboBox>("AddressBox").SelectedItem as Address;if(a!=null){try{Clipboard.SetText(a.Value);Text("ConnectionResult","Адрес скопирован: "+a.Value);}catch{Message("Не удалось скопировать адрес. Повторите попытку.");}}};
  Find<Button>("RefreshAddressButton").Click+=(s,e)=>Addresses();Addresses();
  Find<Canvas>("MemoryChart").SizeChanged+=(s,e)=>DrawChart();
  w.Closing+=(s,e)=>{if(Running){e.Cancel=true;if(stopping){Message("Сервер сохраняется и останавливается. Дождитесь завершения.");return;}if(MessageBox.Show(w,"Сохранить мир, остановить сервер и закрыть приложение? Пока сервер выключен, друзья не смогут играть.","Закрыть панель",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes){closeAfter=true;Stop();}}};
  w.Closed+=(s,e)=>{if(timer!=null)timer.Stop();if(appLock!=null)appLock.Dispose();};
  timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(s,e)=>Tick();timer.Start();Tick();
  if(capturePath!=null)w.ContentRendered+=(s,e)=>w.Dispatcher.BeginInvoke(new Action(()=>{w.UpdateLayout();var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var fs=File.Create(capturePath))encoder.Save(fs);w.Close();}),DispatcherPriority.ApplicationIdle);
  if(integrationMode)w.ContentRendered+=(s,e)=>BeginIntegration();
  app.Run(w);
 }
 void UpdateRamHint(){int value=(int)Find<Slider>("RamSlider").Value;Text("RamValue","Лимит памяти: "+value+" ГБ");Text("RamAdvice","На этом ПК: "+totalMemory.ToString("0.#")+" ГБ ОЗУ. "+(value>totalMemory-12?"Вы оставляете небольшой запас для Windows и своей игры. Начните с 8 ГБ и следите за показателями.":"Оставьте запас для Windows, своей игры и остальных программ."));}
 bool SaveSettings(){
  if(Running){Message("Сначала остановите сервер кнопкой «Остановить».");return false;}
  int limit;if(!int.TryParse(Find<TextBox>("MaxPlayersBox").Text,out limit)||limit<1||limit>100){Message("Количество мест должно быть целым числом от 1 до 100.");return false;}
  try{int chosen=(int)Find<Slider>("RamSlider").Value;var jp=System.IO.Path.Combine(root,"user_jvm_args.txt");string other=File.Exists(jp)?File.ReadAllText(jp):"";other=Regex.Replace(other,@"(?m)^\s*-Xm[sx]\S+\s*$","").Trim();AtomicWrite(jp,"-Xms1G\r\n-Xmx"+chosen+"G\r\n"+(other.Length>0?other+"\r\n":""));SetProp("max-players",limit.ToString());ram=chosen;maxPlayers=limit;Text("SettingsResult","Сохранено. При следующем запуске: "+ram+" ГБ, мест: "+maxPlayers+".");return true;}catch(Exception ex){Message("Не удалось сохранить настройки: "+ex.Message);return false;}
 }
 void Start(){
  if(Running)return;
  var java=System.IO.Path.Combine(root,"java17","bin","java.exe");var argpath=System.IO.Path.Combine(root,ForgeArgs.Replace('/',System.IO.Path.DirectorySeparatorChar));
  if(!File.Exists(java)||!File.Exists(argpath)||!Directory.Exists(System.IO.Path.Combine(root,"mods"))){Message("Распакуйте весь серверный архив. Приложение должно лежать рядом с папками java17, libraries и mods.");return;}
  try{if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==Port)){Message("Порт "+Port+" уже занят. Возможно, сервер запущен через BAT или другую панель. Сначала остановите ту копию. Две копии одного мира запускать нельзя.");return;}}catch{}
  if(!SaveSettings())return;ReadMemory();
  if(!integrationMode&&availableMemory<ram+2 && MessageBox.Show(w,"Сейчас свободно "+availableMemory.ToString("0.#")+" ГБ, а лимит сервера — "+ram+" ГБ. При заполнении памяти возможны тормоза. Закройте лишние программы или уменьшите лимит. Всё равно запустить?","Мало свободной памяти",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  var eula=System.IO.Path.Combine(root,"eula.txt");bool accepted=File.Exists(eula)&&Regex.IsMatch(File.ReadAllText(eula),@"(?m)^\s*eula\s*=\s*true\s*$");
  if(!accepted){if(!AcceptEula())return;try{AtomicWrite(eula,"# Accepted by the person operating this app.\r\neula=true\r\n");}catch(Exception ex){Message("Не удалось сохранить соглашение: "+ex.Message);return;}}
  try{
   state.Ready=false;state.Oom=false;state.Names.Clear();state.Players=0;state.Tps=double.NaN;state.TpsTime=DateTime.MinValue;state.LagTime=DateTime.MinValue;state.Activity="Запуск Java и модов…";
   heap=double.NaN;heapTime=DateTime.MinValue;pressure=0;history.Clear();lastPlayers="";stopping=false;lastExit=-1;launchedRam=ram;
   var info=new ProcessStartInfo(java,"@user_jvm_args.txt @"+ForgeArgs+" nogui"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
   server=new Process{StartInfo=info,EnableRaisingEvents=true};server.OutputDataReceived+=ReadLine;server.ErrorDataReceived+=ReadLine;server.Start();server.BeginOutputReadLine();server.BeginErrorReadLine();started=lastSample=DateTime.UtcNow;lastCpu=TimeSpan.Zero;ticks=0;
   Find<TextBox>("LogBox").Clear();Text("SettingsResult","");Controls(true);Tick();
  }catch(Exception ex){Message("Сервер не запущен: "+ex.Message);state.Activity="Ошибка запуска Java";Controls(false);}
 }
 bool AcceptEula(){
  var dialog=new Window{Owner=w,Title="Соглашение Minecraft",Width=540,Height=300,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=new SolidColorBrush(Color.FromRgb(25,33,44)),Foreground=Brushes.White,FontFamily=new FontFamily("Segoe UI")};
  var stack=new StackPanel{Margin=new Thickness(24)};stack.Children.Add(new TextBlock{Text="Первый запуск сервера",FontSize=22,FontWeight=FontWeights.SemiBold});stack.Children.Add(new TextBlock{Text="Перед запуском прочитайте соглашение Minecraft. Нажимая «Принимаю и запускаю», вы сами подтверждаете согласие. Закрытие окна отменяет запуск.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,18,0,18),FontSize=14});
  var link=new Button{Content="Открыть minecraft.net/eula",Margin=new Thickness(0,0,0,16)};link.Click+=(s,e)=>OpenPath("https://www.minecraft.net/eula");stack.Children.Add(link);
  var row=new StackPanel{Orientation=Orientation.Horizontal};var ok=new Button{Content="Принимаю и запускаю",Padding=new Thickness(14,9,14,9),Margin=new Thickness(0,0,12,0)};ok.Click+=(s,e)=>dialog.DialogResult=true;var cancel=new Button{Content="Отмена",Padding=new Thickness(14,9,14,9)};cancel.Click+=(s,e)=>dialog.DialogResult=false;row.Children.Add(ok);row.Children.Add(cancel);stack.Children.Add(row);dialog.Content=stack;return dialog.ShowDialog()==true;
 }
 void ReadLine(object sender,DataReceivedEventArgs e){if(sender==server&&e.Data!=null){if(lines.Count<20000)lines.Enqueue(e.Data);}}
 void Send(string command){try{if(Running){server.StandardInput.WriteLine(command);server.StandardInput.Flush();}}catch(Exception ex){state.Activity="Не удалось передать команду: "+ex.Message;}}
 void Stop(){if(!Running)return;if(!stopping){stopping=true;stopAt=DateTime.UtcNow;state.Activity="Сохраняем мир и останавливаем сервер…";Send("stop");Find<Button>("SaveButton").IsEnabled=false;Find<Button>("StopButton").Content="Останавливается…";}
  else if((DateTime.UtcNow-stopAt).TotalSeconds>120 && MessageBox.Show(w,"Сервер не завершился за две минуты. Принудительная остановка может потерять несохранённые изменения мира. Завершить процесс?","Принудительная остановка",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes){try{server.Kill();}catch(Exception ex){Message(ex.Message);}}
 }
 void Controls(bool running){Find<Button>("StartButton").IsEnabled=!running;Find<Button>("StopButton").IsEnabled=running;Find<Button>("SaveButton").IsEnabled=running&&state.Ready&&!stopping;Find<Button>("ApplyButton").IsEnabled=!running;Find<Slider>("RamSlider").IsEnabled=!running;Find<TextBox>("MaxPlayersBox").IsEnabled=!running;if(!running)Find<Button>("StopButton").Content="■  Остановить";}
 void Tick(){
  string line;var builder=new StringBuilder();int n=0;while(n++<3000&&lines.TryDequeue(out line)){state.Parse(line);builder.AppendLine(line);}
  if(builder.Length>0){var box=Find<TextBox>("LogBox");if(box.Text.Length>140000)box.Text=box.Text.Substring(box.Text.Length-80000);box.AppendText(builder.ToString());box.ScrollToEnd();}
  ReadMemory();bool running=Running;DateTime now=DateTime.UtcNow;
  if(!running&&server!=null&&lastExit<0){try{lastExit=server.ExitCode;}catch{lastExit=1;}state.Ready=false;state.Names.Clear();state.Players=0;if(stopping&&lastExit==0)state.Activity="Сервер остановлен. Мир сохранён.";else state.Activity="Сервер завершился, код "+lastExit+". Подробности — во вкладке «Журнал».";stopping=false;Controls(false);if(closeAfter){w.Close();return;}}
  if(running){
   try{server.Refresh();double seconds=(now-lastSample).TotalSeconds;double cpu=seconds>0?(server.TotalProcessorTime-lastCpu).TotalSeconds/seconds/Environment.ProcessorCount*100:0;lastCpu=server.TotalProcessorTime;lastSample=now;Text("CpuMetric",Math.Max(0,Math.Min(100,cpu)).ToString("0.#")+" %");Text("HeapCaption","Процесс в ОЗУ: "+(server.WorkingSet64/1073741824.0).ToString("0.0")+" ГБ · heap отдельно");}catch{}
   ticks++;if(ticks%8==1&&!probing)ProbeHeap();if(state.Ready&&!stopping){Find<Button>("SaveButton").IsEnabled=true;if(ticks%8==0)Send("list");if(ticks%15==0)Send("forge tps");}
   Text("StateLabel",stopping?"●  Остановка":state.Ready?"●  Работает":"●  Запускается");Find<TextBlock>("StateLabel").Foreground=new SolidColorBrush(state.Ready?Color.FromRgb(124,225,183):Color.FromRgb(238,199,119));
   Text("UptimeLabel","Время работы: "+(now-started).ToString(@"hh\:mm\:ss"));Text("RunHint",stopping?"Не выключайте компьютер":"Лимит Java: "+launchedRam+" ГБ");
   if(stopping&&(now-stopAt).TotalSeconds>120)Find<Button>("StopButton").Content="Завершить принудительно";
  }else{Text("StateLabel","●  Остановлен");Find<TextBlock>("StateLabel").Foreground=new SolidColorBrush(Color.FromRgb(166,178,196));Text("UptimeLabel","Мир хранится в папке world");Text("CpuMetric","—");Text("HeapCaption","Показатели появятся после запуска");Text("RunHint","Java 17 уже в комплекте");}
  bool freshHeap=running&&(now-heapTime).TotalSeconds<45&&!double.IsNaN(heap);
  Text("HeapMetric",(freshHeap?heap.ToString("0.0"):"—")+" / "+(running?launchedRam:ram)+" ГБ");
  history.Add(freshHeap?heap:double.NaN);if(history.Count>60)history.RemoveAt(0);DrawChart();
  bool freshTps=running&&(now-state.TpsTime).TotalSeconds<90&&!double.IsNaN(state.Tps);
  Text("TpsMetric",freshTps?state.Tps.ToString("0.#")+" TPS":"— TPS");Text("TpsCaption",freshTps?state.Tick.ToString("0.#")+" мс на тик · цель ≤ 50":"Измерение после загрузки мира");
  Text("PlayersMetric",(running&&state.Ready?state.Players.ToString():"—")+" / "+maxPlayers);Text("PlayersCaption",running?(state.Ready?"Сервер доступен в своей сети":"Дождитесь готовности мира"):"Сервер не запущен");
  string names=string.Join("\n",state.Names.OrderBy(x=>x));if(names!=lastPlayers){Find<ListBox>("PlayerList").ItemsSource=state.Names.OrderBy(x=>x).ToList();lastPlayers=names;}
  Text("ActivityLabel",state.Activity);Text("SystemMemoryLabel","На ПК свободно "+availableMemory.ToString("0.0")+" из "+totalMemory.ToString("0.#")+" ГБ ОЗУ");
  string advice=ServerState.Advice(running,state.Ready,state.Oom,freshHeap?heap:double.NaN,running?launchedRam:ram,availableMemory,freshTps?state.Tps:double.NaN,freshHeap?pressure:0,state.Players,maxPlayers,(now-state.LagTime).TotalSeconds<90);
  if(advice!=lastAdvice){Text("AdviceText",advice);lastAdvice=advice;}
  Text("FooterLabel",running?"Обновление: "+DateTime.Now.ToString("HH:mm:ss")+" · heap ≈ каждые 16 с":"Панель управляет только сервером из своей папки");
 }
 void ProbeHeap(){
  var tool=System.IO.Path.Combine(root,"java17","bin","jcmd.exe");if(!File.Exists(tool)||!Running)return;probing=true;int pid=server.Id;
  Task.Run(()=>{double value=double.NaN;try{using(var p=new Process()){p.StartInfo=new ProcessStartInfo(tool,pid+" GC.heap_info"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};p.Start();var a=p.StandardOutput.ReadToEndAsync();var b=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(5000)){try{p.Kill();}catch{}}else value=ServerState.Heap(a.Result);}}catch{}
   try{w.Dispatcher.BeginInvoke(new Action(()=>{probing=false;if(!Running||server.Id!=pid)return;if(!double.IsNaN(value)){heap=value;heapTime=DateTime.UtcNow;pressure=state.Ready&&heap/launchedRam>=0.88?pressure+1:0;}}));}catch{}
  });
 }
 void DrawChart(){var c=Find<Canvas>("MemoryChart");if(c==null)return;c.Children.Clear();double width=c.ActualWidth,height=c.ActualHeight;if(width<10||height<10)return;double max=Running?launchedRam:ram;
  for(int i=0;i<3;i++){double y=height*i/2;var l=new Line{X1=0,X2=width,Y1=y,Y2=y,Stroke=new SolidColorBrush(Color.FromRgb(44,56,72)),StrokeThickness=1};c.Children.Add(l);}
  Polyline poly=null;for(int i=0;i<history.Count;i++){if(double.IsNaN(history[i])){poly=null;continue;}if(poly==null){poly=new Polyline{Stroke=new SolidColorBrush(Color.FromRgb(124,225,183)),StrokeThickness=2.5};c.Children.Add(poly);}poly.Points.Add(new Point(width*i/59,height-Math.Min(1,history[i]/max)*height));}
  if(!history.Any(v=>!double.IsNaN(v))){var text=new TextBlock{Text="График появится после замеров Java",Foreground=new SolidColorBrush(Color.FromRgb(111,128,151)),FontSize=12};Canvas.SetTop(text,height/2-8);Canvas.SetLeft(text,10);c.Children.Add(text);}
 }
 sealed class Address{public string Value,Label;public override string ToString(){return Label+" — "+Value;}}
 void Addresses(){var box=Find<ComboBox>("AddressBox");box.Items.Clear();try{foreach(var nic in NetworkInterface.GetAllNetworkInterfaces().Where(a=>a.OperationalStatus==OperationalStatus.Up&&a.NetworkInterfaceType!=NetworkInterfaceType.Loopback)){foreach(var a in nic.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254.")))box.Items.Add(new Address{Label=nic.Name,Value=a.Address+":"+Port});}}catch{}box.Items.Add(new Address{Label="Только для хоста на этом ПК",Value="localhost:"+Port});box.SelectedIndex=0;}
 void OpenPath(string path){try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){Message(ex.Message);}}
 void BeginIntegration(){
  if(!File.Exists(System.IO.Path.Combine(root,"MOCK-ONLY")))throw new InvalidOperationException("Integration requires a mock fixture");
  Start();DateTime begin=DateTime.UtcNow;bool verified=false;var testTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
  testTimer.Tick+=(s,e)=>{try{
   if(Running&&state.Ready&&!verified){Send("list");Send("forge tps");if(!probing)ProbeHeap();if(state.Players==2&&state.Names.Contains("Alex")&&state.Tps==20&&!double.IsNaN(heap)&&Math.Abs(heap-1)<0.001){verified=true;Stop();}}
   if(verified&&!Running){if(!File.Exists(System.IO.Path.Combine(root,"mock-graceful-stop.txt")))throw new Exception("Graceful shutdown marker missing");if(!File.ReadAllText(System.IO.Path.Combine(root,"user_jvm_args.txt")).Contains("-Dkeep.test=true"))throw new Exception("Custom JVM option lost");File.WriteAllText(integrationResult,"PASS: real redirected child process, ready-state, player list, TPS command response, Java heap probe, graceful stop, settings saved, custom JVM flag preserved.");testTimer.Stop();w.Close();}
   else if((DateTime.UtcNow-begin).TotalSeconds>25)throw new Exception("Integration timed out");
  }catch(Exception ex){testTimer.Stop();if(Running)try{server.Kill();server.WaitForExit(3000);}catch{}File.WriteAllText(integrationResult,"FAIL: "+ex);w.Close();}};
  testTimer.Start();
 }
 [STAThread]public static int Main(string[] args){
  if(args.Length>0&&args[0]=="--self-test")return SelfTest(args.Length>1?args[1]:"panel-self-test.txt");
  if(args.Length>1&&args[0]=="--capture")capturePath=System.IO.Path.GetFullPath(args[1]);
  if(args.Length>2&&args[0]=="--integration-test"){integrationMode=true;integrationResult=System.IO.Path.GetFullPath(args[2]);try{string fixture=System.IO.Path.GetFullPath(args[1]);if(!File.Exists(System.IO.Path.Combine(fixture,"MOCK-ONLY")))return 2;new PanelApp(fixture).Run();return File.Exists(integrationResult)&&File.ReadAllText(integrationResult).StartsWith("PASS")?0:1;}catch(Exception ex){File.WriteAllText(integrationResult,"FAIL: "+ex);return 1;}}
  try{new PanelApp(AppDomain.CurrentDomain.BaseDirectory).Run();return 0;}catch(Exception ex){try{File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"panel-error.txt"),ex.ToString());}catch{}MessageBox.Show("Не удалось открыть панель: "+ex.Message,"TFG");return 1;}
 }
 static int SelfTest(string path){try{
  var s=new ServerState();s.Parse("[Server thread/INFO]: Done (15.2s)! For help, type \"help\"");if(!s.Ready)throw new Exception("readiness");
  s.Parse("[Server thread/INFO]: There are 2 of a max of 10 players online: Alex, Steve");if(s.Players!=2||!s.Names.Contains("Alex"))throw new Exception("list");
  s.Parse("[Server thread/INFO]: Alex left the game");if(s.Players!=1)throw new Exception("leave");s.Parse("[Server thread/INFO]: Friend joined the game");if(s.Players!=2)throw new Exception("join");
  s.Parse("[Server thread/INFO]: Overall: Mean tick time: 12.34 ms. Mean TPS: 20.000");if(s.Tps!=20||s.Tick!=12.34)throw new Exception("tps");
  if(Math.Abs(ServerState.Heap(" garbage-first heap   total 2097152K, used 1048576K [foo]")-1)>0.001)throw new Exception("heap");if(!double.IsNaN(ServerState.Heap("error")))throw new Exception("heap invalid");
  if(!ServerState.Advice(true,true,false,7.5,8,8,20,3,2,10,false).Contains("ещё 2 ГБ"))throw new Exception("memory advice");
  if(ServerState.Advice(true,true,false,3,8,8,15,0,9,10,false).Contains("ещё 2 ГБ"))throw new Exception("cpu mistaken for ram");
  if(!ServerState.Advice(true,true,false,7.5,8,1,15,3,9,10,false).Contains("увеличивать память серверу не стоит"))throw new Exception("system memory guard");
  File.WriteAllText(path,"PASS: readiness, player sync/join/leave, TPS, heap metrics, malformed heap, sustained memory pressure, CPU distinction, low system memory.\r\n",Encoding.UTF8);return 0;
 }catch(Exception e){File.WriteAllText(path,"FAIL: "+e,Encoding.UTF8);return 1;}}
}
