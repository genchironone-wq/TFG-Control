using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

public sealed partial class PanelApp {
 PerspectiveCamera planetCamera; RotateTransform3D planetRotation; AxisAngleRotation3D planetAngle;
 bool rotatingPlanet; Point lastPlanetPoint; readonly Dictionary<string,MeshGeometry3D> voxelMeshes=new Dictionary<string,MeshGeometry3D>();
 readonly HashSet<string> voxels=new HashSet<string>();
 static string VoxelKey(int x,int y,int z){return x+","+y+","+z;}
 static uint NameHash(string name){uint h=2166136261;foreach(char c in name){h^=c;h*=16777619;}return h;}
 void BuildPlanet(){
  var viewport=Find<Viewport3D>("PlanetViewport");if(viewport==null)return;
  planetCamera=new PerspectiveCamera(new Point3D(7,4.6,9),new Vector3D(-7,-4.6,-9),new Vector3D(0,1,0),34);viewport.Camera=planetCamera;
  var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Color.FromRgb(117,123,126)));scene.Children.Add(new DirectionalLight(Color.FromRgb(216,204,181),new Vector3D(0.2,-1,-0.5)));scene.Children.Add(new DirectionalLight(Color.FromRgb(89,110,125),new Vector3D(-1,0.1,0.4)));
  for(int x=-5;x<=5;x++)for(int y=-5;y<=5;y++)for(int z=-5;z<=5;z++)if(x*x+y*y+z*z<=28)voxels.Add(VoxelKey(x,y,z));
  for(int x=-5;x<=5;x++)for(int y=-5;y<=5;y++)for(int z=-5;z<=5;z++)if(voxels.Contains(VoxelKey(x,y,z))){
   int variation=Math.Abs(x*17+y*31+z*13)%3;bool water=y>=0&&((x<-2&&z>0)||(z>2&&x<2));
   for(int face=0;face<6;face++){
    int nx=x+(face==0?1:face==1?-1:0),ny=y+(face==2?1:face==3?-1:0),nz=z+(face==4?1:face==5?-1:0);if(voxels.Contains(VoxelKey(nx,ny,nz)))continue;
    string color;
    if(water)color=new[]{"#537B88","#5B8792","#486D7F"}[variation];
    else if(y>=0&&face==2)color=new[]{"#799272","#6B8462","#8A9A76"}[variation];
    else if(y>=1&&face!=3)color=new[]{"#8E8065","#9A886B","#7D745F"}[variation];
    else if(y>=-2)color=new[]{"#897663","#786A5B","#96816B"}[variation];
    else color=new[]{"#727B7B","#657172","#7D8786"}[variation];
    AddVoxelFace(x,y,z,face,color);
   }
  }
  var globe=new Model3DGroup();foreach(var pair in voxelMeshes){pair.Value.Freeze();var material=new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Key)));material.Freeze();globe.Children.Add(new GeometryModel3D(pair.Value,material));}
  AddTree(globe,1,1,4);AddTree(globe,-1,-2,4);AddTree(globe,3,0,4);
  planetAngle=new AxisAngleRotation3D(new Vector3D(0,1,0),-12);planetRotation=new RotateTransform3D(planetAngle);globe.Transform=planetRotation;scene.Children.Add(globe);viewport.Children.Add(new ModelVisual3D{Content=scene});
  var host=Find<Grid>("PlanetHost");host.SizeChanged+=(s,e)=>RenderPlanetPlayers();host.MouseLeftButtonDown+=(s,e)=>{rotatingPlanet=true;lastPlanetPoint=e.GetPosition(host);host.CaptureMouse();};
  host.MouseMove+=(s,e)=>{if(rotatingPlanet){var p=e.GetPosition(host);planetAngle.Angle+=(p.X-lastPlanetPoint.X)*0.55;lastPlanetPoint=p;RenderPlanetPlayers();}};
  host.MouseLeftButtonUp+=(s,e)=>{rotatingPlanet=false;host.ReleaseMouseCapture();};host.LostMouseCapture+=(s,e)=>rotatingPlanet=false;
 }
 void AddTree(Model3DGroup globe,int x,int z,int y){
  while(voxels.Contains(VoxelKey(x,y+1,z)))y++;double bx=x*0.34,bz=z*0.34,by=(y+0.5)*0.34;
  AddDetailCube(globe,new Point3D(bx,by+0.16,bz),0.15,0.34,0.15,"#806B55");
  AddDetailCube(globe,new Point3D(bx,by+0.42,bz),0.43,0.31,0.43,"#5F7D60");
  AddDetailCube(globe,new Point3D(bx,by+0.60,bz),0.29,0.19,0.29,"#738A66");
 }
 void AddDetailCube(Model3DGroup globe,Point3D c,double sx,double sy,double sz,string color){
  var m=new MeshGeometry3D();double x=sx/2,y=sy/2,z=sz/2;
  var ps=new[]{new Point3D(c.X-x,c.Y-y,c.Z-z),new Point3D(c.X+x,c.Y-y,c.Z-z),new Point3D(c.X+x,c.Y+y,c.Z-z),new Point3D(c.X-x,c.Y+y,c.Z-z),new Point3D(c.X-x,c.Y-y,c.Z+z),new Point3D(c.X+x,c.Y-y,c.Z+z),new Point3D(c.X+x,c.Y+y,c.Z+z),new Point3D(c.X-x,c.Y+y,c.Z+z)};
  int[][] faces={new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,4,7,3},new[]{1,2,6,5},new[]{3,7,6,2},new[]{0,1,5,4}};
  foreach(var f in faces){int n=m.Positions.Count;foreach(int i in f)m.Positions.Add(ps[i]);foreach(int i in new[]{0,1,2,0,2,3})m.TriangleIndices.Add(n+i);}m.Freeze();var material=new DiffuseMaterial(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));globe.Children.Add(new GeometryModel3D(m,material));
 }
 void AddVoxelFace(int x,int y,int z,int face,string color){
  MeshGeometry3D mesh;if(!voxelMeshes.TryGetValue(color,out mesh)){mesh=new MeshGeometry3D();voxelMeshes[color]=mesh;}
  double s=0.34,h=s*0.485,cx=x*s,cy=y*s,cz=z*s;Point3D[] p;
  switch(face){
   case 0:p=new[]{new Point3D(cx+h,cy-h,cz+h),new Point3D(cx+h,cy-h,cz-h),new Point3D(cx+h,cy+h,cz-h),new Point3D(cx+h,cy+h,cz+h)};break;
   case 1:p=new[]{new Point3D(cx-h,cy-h,cz-h),new Point3D(cx-h,cy-h,cz+h),new Point3D(cx-h,cy+h,cz+h),new Point3D(cx-h,cy+h,cz-h)};break;
   case 2:p=new[]{new Point3D(cx-h,cy+h,cz+h),new Point3D(cx+h,cy+h,cz+h),new Point3D(cx+h,cy+h,cz-h),new Point3D(cx-h,cy+h,cz-h)};break;
   case 3:p=new[]{new Point3D(cx-h,cy-h,cz-h),new Point3D(cx+h,cy-h,cz-h),new Point3D(cx+h,cy-h,cz+h),new Point3D(cx-h,cy-h,cz+h)};break;
   case 4:p=new[]{new Point3D(cx-h,cy-h,cz+h),new Point3D(cx+h,cy-h,cz+h),new Point3D(cx+h,cy+h,cz+h),new Point3D(cx-h,cy+h,cz+h)};break;
   default:p=new[]{new Point3D(cx+h,cy-h,cz-h),new Point3D(cx-h,cy-h,cz-h),new Point3D(cx-h,cy+h,cz-h),new Point3D(cx+h,cy+h,cz-h)};break;
  }
  int n=mesh.Positions.Count;foreach(var point in p)mesh.Positions.Add(point);mesh.TriangleIndices.Add(n);mesh.TriangleIndices.Add(n+1);mesh.TriangleIndices.Add(n+2);mesh.TriangleIndices.Add(n);mesh.TriangleIndices.Add(n+2);mesh.TriangleIndices.Add(n+3);
 }
 void RenderPlanetPlayers(){
  var overlay=Find<Canvas>("PlanetOverlay");if(overlay==null||planetCamera==null)return;overlay.Children.Clear();double width=overlay.ActualWidth,height=overlay.ActualHeight;if(width<10||height<10)return;
  var names=state.Names.OrderBy(n=>n).ToList();Text("PlanetCaption",names.Count==0?"Игроки появятся здесь после входа":names.Count+" в игре · позиции условные");
  Vector3D forward=planetCamera.LookDirection;forward.Normalize();Vector3D right=Vector3D.CrossProduct(forward,planetCamera.UpDirection);right.Normalize();Vector3D up=Vector3D.CrossProduct(right,forward);up.Normalize();double focal=width/(2*Math.Tan(planetCamera.FieldOfView*Math.PI/360));
  int visible=Math.Min(10,names.Count);
  for(int i=0;i<visible;i++){
   string name=names[i];uint hash=NameHash(name);double angle=(52+(visible==1?0:-57+i*114.0/(visible-1)))*Math.PI/180;double y=-0.3+(hash%100)/100.0*1.75;double radius=2.05;double circle=Math.Sqrt(radius*radius-y*y);var pos=planetRotation.Transform(new Point3D(circle*Math.Cos(angle),y,circle*Math.Sin(angle)));
   if(Vector3D.DotProduct((Vector3D)pos,(Vector3D)planetCamera.Position)<-0.15)continue;
   var delta=pos-planetCamera.Position;double depth=Vector3D.DotProduct(delta,forward);if(depth<=0)continue;double screenX=width/2+Vector3D.DotProduct(delta,right)*focal/depth,screenY=height/2-Vector3D.DotProduct(delta,up)*focal/depth;
   var marker=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,ToolTip=name+" — игрок онлайн. Позиция на планете условная."};
   var label=new Border{Background=new SolidColorBrush(Color.FromRgb(31,38,43)),BorderBrush=new SolidColorBrush(Color.FromRgb(100,112,119)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(8,4,8,4),Margin=new Thickness(0,0,0,4),Child=new TextBlock{Text=name,Foreground=new SolidColorBrush(Color.FromRgb(226,224,217)),FontSize=10,FontWeight=FontWeights.SemiBold}};
   label.Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=10,ShadowDepth=2,Opacity=0.4};marker.Children.Add(label);
   var head=new Grid{Width=15,Height=15,HorizontalAlignment=HorizontalAlignment.Center};head.Children.Add(new Rectangle{RadiusX=2,RadiusY=2,Fill=new SolidColorBrush(Color.FromRgb((byte)(167+hash%35),(byte)(140+hash%39),(byte)(103+hash%45))),Stroke=new SolidColorBrush(Color.FromRgb(232,209,176)),StrokeThickness=1});
   var eyes=new TextBlock{Text="··",Foreground=new SolidColorBrush(Color.FromRgb(50,49,44)),FontSize=16,FontWeight=FontWeights.Bold,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,-5,0,0)};head.Children.Add(eyes);marker.Children.Add(head);marker.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Canvas.SetLeft(marker,Math.Max(0,Math.Min(width-marker.DesiredSize.Width,screenX-marker.DesiredSize.Width/2)));Canvas.SetTop(marker,Math.Max(0,Math.Min(height-marker.DesiredSize.Height,screenY-marker.DesiredSize.Height+8)));overlay.Children.Add(marker);
  }
 }
}
