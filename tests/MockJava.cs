using System;
using System.IO;
class MockJava {
 static void Main(string[] args){
  if(args.Length>1&&args[1]=="GC.heap_info"){Console.WriteLine("garbage-first heap   total 2097152K, used 1048576K [test]");return;}
  Console.WriteLine("[Server thread/INFO]: Done (1.2s)! For help, type \"help\"");
  string line;while((line=Console.ReadLine())!=null){
   if(line=="list")Console.WriteLine("[Server thread/INFO]: There are 2 of a max of 10 players online: Alex, Steve");
   if(line=="forge tps")Console.WriteLine("[Server thread/INFO]: Overall: Mean tick time: 12.34 ms. Mean TPS: 20.000");
   if(line=="stop"){File.WriteAllText("mock-graceful-stop.txt","saved");Console.WriteLine("[Server thread/INFO]: Stopping server");return;}
  }
 }
}
