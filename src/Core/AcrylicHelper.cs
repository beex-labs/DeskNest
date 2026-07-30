using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BeeX.DeskNest;
static class AcrylicHelper
{
 enum AccentState{Disabled=0,TransparentGradient=2,BlurBehind=3,AcrylicBlurBehind=4}
 [StructLayout(LayoutKind.Sequential)]struct AccentPolicy{public AccentState State;public int Flags;public uint GradientColor;public int AnimationId;}
 [StructLayout(LayoutKind.Sequential)]struct WindowCompositionAttributeData{public int Attribute;public IntPtr Data;public int Size;}
 [DllImport("user32.dll")]static extern int SetWindowCompositionAttribute(IntPtr hwnd,ref WindowCompositionAttributeData data);
 public static void Apply(Window window,bool enabled,double opacity=.5,bool dark=false)
 {
  try
  {
   var policy=new AccentPolicy{State=AccentState.Disabled,Flags=0,GradientColor=0};
   var size=Marshal.SizeOf<AccentPolicy>();var ptr=Marshal.AllocHGlobal(size);
   try{Marshal.StructureToPtr(policy,ptr,false);var data=new WindowCompositionAttributeData{Attribute=19,Data=ptr,Size=size};SetWindowCompositionAttribute(new WindowInteropHelper(window).Handle,ref data);}
   finally{Marshal.FreeHGlobal(ptr);}
  }
  catch{}
 }
}
