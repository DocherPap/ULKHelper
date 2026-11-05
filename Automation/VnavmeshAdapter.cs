using System.Numerics;
using Dalamud.Plugin;

namespace ULKHelper.Automation;

internal sealed class VnavmeshAdapter(IDalamudPluginInterface plugin) : INavigationMesh
{
    public bool SupportsRouteEstimates => true;
    public bool Ready => plugin.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
    public bool Busy => plugin.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress").InvokeFunc()
        || plugin.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc();
    public IReadOnlyList<Vector3> Route => plugin.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints").InvokeFunc();
    public Vector3? Ground(Vector3 position, bool unknownHeight)
    {
        var floor=plugin.GetIpcSubscriber<Vector3,bool,float,Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        if(unknownHeight) return floor.InvokeFunc(new(position.X,1024,position.Z),false,5);
        var near=plugin.GetIpcSubscriber<Vector3,float,float,Vector3?>("vnavmesh.Query.Mesh.NearestPoint").InvokeFunc(position,3,8);
        if(near!=null)return near;
        var below=floor.InvokeFunc(position+new Vector3(0,3,0),false,5);
        return below is {} p && Math.Abs(p.Y-position.Y)<=8 ? below : null;
    }
    public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, CancellationToken cancellation)
        => plugin.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable")
            .InvokeFunc(from, to, false, cancellation);
    public void Move(List<Vector3> route) => plugin.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo").InvokeAction(route, false);
    public void Stop() => plugin.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction();
}
