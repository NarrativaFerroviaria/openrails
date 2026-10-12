// ORTS Flexible Connections V5
// Purpose: Publisher-owned open accordion meshes, curved guide, sealed frame snapshots
// and independent GPU lifetime. No catenary, physics, caps or disconnected geometry.
// Loader constructs topology/materials; Updater builds private snapshots; Render alone
// creates, uploads and disposes buffers. Retired owners cannot publish new frames.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Simulation.RollingStocks;
using Orts.Viewer3D.Common;
using ORTS.Common;

namespace Orts.Viewer3D.RollingStock
{
    internal sealed class FlexibleConnectionBellowMesh
    {
        internal readonly int Perimeter, Bands, VertexCount;
        internal readonly ushort[] Indices;
        readonly Vector2[] Outer, Inner;
        readonly float[] OuterU, InnerU;
        readonly Vector3[] Rings;
        readonly int[] OuterGroups, InnerGroups;
        readonly Vector3[] GroupNormals;
        readonly double[] Arc = new double[4097];
        Vector3 Delta, TangentA, TangentB;
        int ArcSteps;

        internal FlexibleConnectionBellowMesh(FlexibleConnectionBellowConfig config)
        {
            Perimeter = 4 * (config.CornerSegments + 1);
            Bands = 2 * config.Folds;
            VertexCount = 2 * (Perimeter + 1) * Bands;
            Outer = Contour(config.OuterWidthM, config.OuterHeightM, config.CornerRadiusM, config.CornerSegments);
            Inner = Contour(config.InnerWidthM, config.InnerHeightM,
                Math.Min(config.CornerRadiusM, Math.Min(config.InnerWidthM, config.InnerHeightM) / 2), config.CornerSegments);
            OuterU = PerimeterCoordinates(Outer); InnerU = PerimeterCoordinates(Inner);
            OuterGroups = NormalGroups(Outer); InnerGroups = NormalGroups(Inner);
            GroupNormals = new Vector3[Perimeter];
            Rings = new Vector3[(Bands + 1) * Perimeter];
            var indices = new List<ushort>(6 * Perimeter * Bands);
            for (int band = 0; band < Bands; band++)
            {
                var lo = (band & 1) == 0 ? Outer : Inner;
                var hi = (band & 1) == 0 ? Inner : Outer;
                for (int j = 0; j < Perimeter; j++)
                {
                    int k = (j + 1) % Perimeter;
                    int a = band * 2 * (Perimeter + 1) + j, b = a + 1;
                    int c = a + Perimeter + 1, d = c + 1;
                    // Fixed collapsed contour edges need no triangle. Correspondence is retained.
                    if (hi[j] != hi[k]) { indices.Add((ushort)a); indices.Add((ushort)c); indices.Add((ushort)d); }
                    if (lo[j] != lo[k]) { indices.Add((ushort)a); indices.Add((ushort)d); indices.Add((ushort)b); }
                }
            }
            Indices = indices.ToArray();
        }

        static Vector2[] Contour(float width, float height, float radius, int segments)
        {
            var points = new Vector2[4 * (segments + 1)];
            for (int corner = 0; corner < 4; corner++)
            {
                double cx = (corner < 2 ? 1 : -1) * ((double)width / 2 - radius);
                double cy = (corner == 0 || corner == 3 ? -1 : 1) * ((double)height / 2 - radius);
                for (int j = 0; j <= segments; j++)
                {
                    double angle = (-1 + corner) * Math.PI / 2 + j * (Math.PI / 2 / segments);
                    // Exact tangent endpoints avoid microscopic edges at the maximum radius.
                    double x = Math.Cos(angle), y = Math.Sin(angle);
                    if (j == 0 || j == segments)
                    {
                        int axis = (corner + (j == segments ? 1 : 0) + 3) % 4;
                        x = axis == 0 ? 1 : axis == 2 ? -1 : 0;
                        y = axis == 1 ? 1 : axis == 3 ? -1 : 0;
                    }
                    points[corner * (segments + 1) + j] = new Vector2((float)(cx + radius * x), (float)(cy + radius * y));
                }
            }
            return points;
        }

        static float[] PerimeterCoordinates(Vector2[] points)
        {
            var result = new float[points.Length + 1];
            double total = 0;
            for (int j = 0; j < points.Length; j++)
            {
                var next = points[(j + 1) % points.Length];
                double x = (double)next.X - points[j].X, y = (double)next.Y - points[j].Y;
                total += Math.Sqrt(x * x + y * y); result[j + 1] = (float)total;
            }
            if (!(total > 0) || double.IsInfinity(total) || !Finite(result[result.Length - 1]))
                throw new ArgumentException("Bellows contour is not representable.");
            for (int j = 1; j < result.Length; j++) result[j] = (float)(result[j] / total);
            result[result.Length - 1] = 1;
            return result;
        }

        static int[] NormalGroups(Vector2[] points)
        {
            var groups = new int[points.Length + 1];
            for (int j = 0; j < points.Length; j++)
            {
                groups[j] = j;
                for (int k = 0; k < j; k++)
                    if (points[k] == points[j]) { groups[j] = groups[k]; break; }
            }
            groups[points.Length] = groups[0];
            return groups;
        }

        internal VertexPositionNormalTexture[] CreateVertices()
        {
            var vertices = new VertexPositionNormalTexture[VertexCount];
            for (int band = 0; band < Bands; band++)
                for (int side = 0; side < 2; side++)
                {
                    int ring = band + side;
                    var u = (ring & 1) == 0 ? OuterU : InnerU;
                    for (int j = 0; j <= Perimeter; j++)
                        vertices[(band * 2 + side) * (Perimeter + 1) + j].TextureCoordinate = new Vector2(u[j], (float)ring / Bands);
                }
            return vertices;
        }

        Vector3 Evaluate(double t)
        {
            double t2 = t * t, t3 = t2 * t;
            double h1 = -2 * t3 + 3 * t2, h2 = t3 - 2 * t2 + t, h3 = t3 - t2;
            return new Vector3((float)(h1 * Delta.X + h2 * TangentA.X + h3 * TangentB.X),
                (float)(h1 * Delta.Y + h2 * TangentA.Y + h3 * TangentB.Y),
                (float)(h1 * Delta.Z + h2 * TangentA.Z + h3 * TangentB.Z));
        }

        internal static double Length(Vector3 v) { return Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z); }
        internal static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        internal static bool Finite(Vector3 v) { return Finite(v.X) && Finite(v.Y) && Finite(v.Z); }
        internal static bool Unit(ref Vector3 v)
        {
            double n = Length(v);
            if (!Finite(v) || n < 1e-12 || double.IsInfinity(n)) return false;
            v = new Vector3((float)(v.X / n), (float)(v.Y / n), (float)(v.Z / n)); return true;
        }

        bool MeasureGuide()
        {
            double previous = -1;
            for (int steps = 32; steps <= 4096; steps *= 2)
            {
                Vector3 last = Vector3.Zero; Arc[0] = 0;
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 p = Evaluate((double)i / steps);
                    if (!Finite(p)) return false;
                    double distance = Length(p - last);
                    if (!(distance > 0) || double.IsInfinity(distance)) return false;
                    Arc[i] = Arc[i - 1] + distance; last = p;
                }
                ArcSteps = steps;
                // Bounded chord-length refinement; independent of rendered ring count.
                if (previous > 0 && Math.Abs(Arc[steps] - previous) <= 1e-6 * previous) return true;
                previous = Arc[steps];
            }
            return false;
        }

        internal bool Build(Vector3 delta, Quaternion qa, Quaternion qb, VertexPositionNormalTexture[] vertices)
        {
            if (!Finite(delta) || vertices.Length != VertexCount) return false;
            double distance = Length(delta);
            if (!(distance > 1e-7) || distance > float.MaxValue) return false;
            Delta = delta;
            TangentA = Vector3.Transform(Vector3.UnitZ, qa) * (float)distance;
            TangentB = Vector3.Transform(Vector3.UnitZ, qb) * (float)distance;
            if (!MeasureGuide()) return false;
            int cursor = 1;
            for (int i = 0; i <= Bands; i++)
            {
                double fraction = (double)i / Bands, target = fraction * Arc[ArcSteps];
                while (cursor < ArcSteps && Arc[cursor] < target) cursor++;
                double t = (cursor - 1 + (target - Arc[cursor - 1]) / (Arc[cursor] - Arc[cursor - 1])) / ArcSteps;
                if (i == 0) t = 0; if (i == Bands) t = 1;
                Vector3 center = i == 0 ? Vector3.Zero : i == Bands ? delta : Evaluate(t);
                Quaternion q;
                if (!Interpolate(qa, qb, fraction, out q)) return false;
                var contour = (i & 1) == 0 ? Outer : Inner;
                for (int j = 0; j < Perimeter; j++)
                {
                    Vector3 p = center + Vector3.Transform(new Vector3(contour[j], 0), q);
                    if (!Finite(p)) return false;
                    Rings[i * Perimeter + j] = p;
                }
            }
            for (int band = 0; band < Bands; band++)
                for (int side = 0; side < 2; side++)
                    for (int j = 0; j <= Perimeter; j++)
                    {
                        int k = (band * 2 + side) * (Perimeter + 1) + j;
                        vertices[k].Position = Rings[(band + side) * Perimeter + j % Perimeter];
                        vertices[k].Normal = Vector3.Zero;
                    }
            for (int i = 0; i < Indices.Length; i += 3)
            {
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                Vector3 n = Vector3.Cross(vertices[c].Position - vertices[a].Position, vertices[b].Position - vertices[a].Position);
                if (!Unit(ref n)) return false;
                vertices[a].Normal += n; vertices[b].Normal += n; vertices[c].Normal += n;
            }
            // Smooth only within a band; folds retain hard creases. Merge coincident
            // contour samples, including UV seam and collapsed corner/straight edges.
            for (int band = 0; band < Bands; band++)
                for (int side = 0; side < 2; side++)
                {
                    var groups = ((band + side) & 1) == 0 ? OuterGroups : InnerGroups;
                    int offset = (band * 2 + side) * (Perimeter + 1);
                    Array.Clear(GroupNormals, 0, GroupNormals.Length);
                    for (int j = 0; j <= Perimeter; j++) GroupNormals[groups[j]] += vertices[offset + j].Normal;
                    for (int j = 0; j < Perimeter; j++)
                    {
                        if (groups[j] == j && !Unit(ref GroupNormals[j])) return false;
                    }
                    for (int j = 0; j <= Perimeter; j++) vertices[offset + j].Normal = GroupNormals[groups[j]];
                }
            return true;
        }

        // Do not force a shortest-path sign each frame: the caller keeps quaternion
        // signs continuous through 180 degrees. Antipodal full turns are unrepresentable.
        static bool Interpolate(Quaternion a, Quaternion b, double t, out Quaternion q)
        {
            double dot = Math.Max(-1, Math.Min(1, Quaternion.Dot(a, b)));
            q = a;
            if (dot < -0.999999) return false;
            double x = 1 - t, y = t;
            if (dot < 0.999999)
            {
                double angle = Math.Acos(dot), sine = Math.Sin(angle);
                x = Math.Sin((1 - t) * angle) / sine; y = Math.Sin(t * angle) / sine;
            }
            q = new Quaternion((float)(x * a.X + y * b.X), (float)(x * a.Y + y * b.Y),
                (float)(x * a.Z + y * b.Z), (float)(x * a.W + y * b.W));
            q.Normalize();
            return Finite(q.X) && Finite(q.Y) && Finite(q.Z) && Finite(q.W);
        }
    }

    internal sealed class FlexibleConnectionBellowViewer
    {
        readonly Viewer Viewer;
        readonly FlexibleConnectionBellowManager Manager;
        readonly FlexibleConnectionBellowOwner Owner;
        readonly Dictionary<FlexibleConnectionPoint, FlexibleConnectionBellowConnection> Connections = new Dictionary<FlexibleConnectionPoint, FlexibleConnectionBellowConnection>();
        readonly HashSet<string> Warnings = new HashSet<string>();

        internal FlexibleConnectionBellowViewer(Viewer viewer, FlexibleConnectionConfig config, string folder)
        {
            Viewer = viewer; Manager = viewer.RenderProcess.FlexibleConnections.Bellows;
            Owner = new FlexibleConnectionBellowOwner();
            var materials = new Dictionary<FlexibleConnectionLocalProfile, Material>();
            foreach (var point in config.ConnectedPoints)
            {
                var profile = point.Profile;
                if (profile.Bellow == null || !profile.Bellow.CanPublish) continue;
                try
                {
                    Material material;
                    if (!materials.TryGetValue(profile, out material))
                    {
                        var fallback = new SolidColorMaterial(viewer, 1, 1, 1, 0);
                        material = FlexibleConnectionViewer.LoadConnectionMaterial(viewer, profile.Name, profile.Bellow.TextureName, folder, fallback);
                        materials.Add(profile, material);
                    }
                    var connection = new FlexibleConnectionBellowConnection(Owner, new FlexibleConnectionBellowMesh(profile.Bellow), material);
                    Connections.Add(point, connection); Owner.Connections.Add(connection);
                }
                catch (ArgumentException error)
                { Trace.TraceWarning("Bellows {0}:{1}: geometry cannot be represented: {2}", point.End, point.ID, error.Message); }
            }
            Manager.Register(Owner);
        }

        internal bool PrepareConnected(RenderFrame frame, FlexibleConnectionMapping mapping, MSTSWagon local,
            MSTSWagon remote, FlexibleConnectionPoint remotePoint)
        {
            if (remotePoint == null || remotePoint.Profile.Bellow == null)
            { Warn(mapping, "missing or non-Bellows remote point"); return false; }
            FlexibleConnectionBellowConnection connection;
            if (!Connections.TryGetValue(mapping.Point, out connection))
            { Warn(mapping, "selected publisher has no representable BellowGeometry"); return false; }
            var snapshot = Manager.Acquire(connection, frame);
            if (snapshot == null) return false;
            bool valid = false;
            Quaternion qa = Quaternion.Identity, qb = Quaternion.Identity;
            Matrix placement = Matrix.Identity;
            try
            {
                var pa = local.WorldPosition; var pb = remote.WorldPosition;
                var ma = pa.XNAMatrix; var mb = pb.XNAMatrix;
                Vector3 a = mapping.Point.Position, b = remotePoint.Position;
                a.Z = -a.Z; b.Z = -b.Z;
                a = Vector3.Transform(a, ma); b = Vector3.Transform(b, mb);
                Vector3 delta = new Vector3((float)((double)b.X - a.X + ((double)pb.TileX - pa.TileX) * WorldPosition.TileSize),
                    (float)((double)b.Y - a.Y), (float)((double)b.Z - a.Z - ((double)pb.TileZ - pa.TileZ) * WorldPosition.TileSize));
                if (Frame(ma, mapping.Point.End, false, out qa) && Frame(mb, remotePoint.End, true, out qb))
                {
                    bool history = ReferenceEquals(connection.Remote, remote) && ReferenceEquals(connection.RemotePoint, remotePoint) && connection.HasHistory;
                    if (history)
                    {
                        if (Quaternion.Dot(qa, connection.PreviousA) < 0) qa = Negate(qa);
                        if (Quaternion.Dot(qb, connection.PreviousB) < 0) qb = Negate(qb);
                    }
                    else if (Quaternion.Dot(qa, qb) < 0) qb = Negate(qb);
                    valid = connection.Mesh.Build(delta, qa, qb, snapshot.Vertices);
                }
                Vector3 offset = new Vector3((float)(((double)pa.TileX - Viewer.Camera.TileX) * WorldPosition.TileSize), 0,
                    (float)(-((double)pa.TileZ - Viewer.Camera.TileZ) * WorldPosition.TileSize));
                placement = Matrix.CreateTranslation(a + offset);
                valid &= FlexibleConnectionBellowMesh.Finite(placement.Translation);
            }
            finally { valid = Manager.Complete(snapshot, frame, valid); }
            if (!valid) { Warn(mapping, "pose or guide is not representable; publication skipped"); return false; }
            connection.Remote = remote; connection.RemotePoint = remotePoint;
            connection.PreviousA = qa; connection.PreviousB = qb; connection.HasHistory = true;
            connection.Published = true;
            frame.AddPrimitive(connection.Material, snapshot.Primitive, RenderPrimitiveGroup.World, ref placement, ShapeFlags.None);
            return true;
        }

        static Quaternion Negate(Quaternion q) { return new Quaternion(-q.X, -q.Y, -q.Z, -q.W); }
        internal static bool Frame(Matrix matrix, FlexibleConnectionEnd end, bool remote, out Quaternion rotation)
        {
            rotation = Quaternion.Identity;
            Vector3 z = Vector3.TransformNormal(end == FlexibleConnectionEnd.Rear ? Vector3.UnitZ : -Vector3.UnitZ, matrix);
            if (remote) z = -z;
            Vector3 up = matrix.Up;
            if (!FlexibleConnectionBellowMesh.Unit(ref z) || !FlexibleConnectionBellowMesh.Unit(ref up)) return false;
            Vector3 x = Vector3.Cross(up, z);
            if (!FlexibleConnectionBellowMesh.Unit(ref x)) return false;
            up = Vector3.Cross(z, x);
            Matrix basis = Matrix.Identity; basis.Right = x; basis.Up = up; basis.Backward = z;
            rotation = Quaternion.CreateFromRotationMatrix(basis); rotation.Normalize();
            return FlexibleConnectionBellowMesh.Finite(rotation.X) && FlexibleConnectionBellowMesh.Finite(rotation.Y) &&
                FlexibleConnectionBellowMesh.Finite(rotation.Z) && FlexibleConnectionBellowMesh.Finite(rotation.W);
        }
        void Warn(FlexibleConnectionMapping mapping, string cause)
        {
            string key = mapping.Point.End + ":" + mapping.LocalPoint + ":" + cause;
            if (Warnings.Add(key)) Trace.TraceWarning("Bellows {0}:{1}: {2}.", mapping.Point.End, mapping.LocalPoint, cause);
        }
        internal void Retire() { Manager.Retire(Owner); }
        internal void BeginFrame()
        {
            foreach (var connection in Owner.Connections)
            {
                // A gap in publication (including uncoupling) ends the orientation history.
                if (!connection.Published) connection.HasHistory = false;
                connection.Published = false;
            }
        }
    }

    internal sealed class FlexibleConnectionBellowOwner
    {
        internal readonly List<FlexibleConnectionBellowConnection> Connections = new List<FlexibleConnectionBellowConnection>();
        internal bool Retired, Prepared;
        internal int References;
    }
    internal sealed class FlexibleConnectionBellowConnection
    {
        internal readonly FlexibleConnectionBellowOwner Owner;
        internal readonly FlexibleConnectionBellowMesh Mesh;
        internal readonly Material Material;
        internal readonly FlexibleConnectionBellowSnapshot First, Second;
        internal DynamicVertexBuffer Buffer;
        internal IndexBuffer IndexBuffer;
        internal VertexBufferBinding[] Bindings;
        internal long Version, Uploaded = -1;
        internal MSTSWagon Remote;
        internal FlexibleConnectionPoint RemotePoint;
        internal Quaternion PreviousA, PreviousB;
        internal bool HasHistory, Published;
        internal FlexibleConnectionBellowConnection(FlexibleConnectionBellowOwner owner, FlexibleConnectionBellowMesh mesh, Material material)
        { Owner = owner; Mesh = mesh; Material = material; First = new FlexibleConnectionBellowSnapshot(this); Second = new FlexibleConnectionBellowSnapshot(this); }
        internal void Prepare(GraphicsDevice device)
        {
            Buffer = new DynamicVertexBuffer(device, VertexPositionNormalTexture.VertexDeclaration, Mesh.VertexCount, BufferUsage.WriteOnly);
            IndexBuffer = new IndexBuffer(device, IndexElementSize.SixteenBits, Mesh.Indices.Length, BufferUsage.WriteOnly);
            IndexBuffer.SetData(Mesh.Indices);
            Bindings = new[] { new VertexBufferBinding(Buffer), new VertexBufferBinding(RenderPrimitive.GetDummyVertexBuffer(device)) };
        }
        internal void Release() { Buffer?.Dispose(); IndexBuffer?.Dispose(); Buffer = null; IndexBuffer = null; }
    }
    internal sealed class FlexibleConnectionBellowSnapshot
    {
        internal readonly FlexibleConnectionBellowConnection Connection;
        internal readonly VertexPositionNormalTexture[] Vertices;
        internal readonly FlexibleConnectionBellowPrimitive Primitive;
        internal FlexibleConnectionBellowManager Manager;
        internal FlexibleConnectionBellowSnapshot Next;
        internal RenderFrame Frame;
        internal bool Building;
        internal long Version;
        internal FlexibleConnectionBellowSnapshot(FlexibleConnectionBellowConnection connection)
        { Connection = connection; Vertices = connection.Mesh.CreateVertices(); Primitive = new FlexibleConnectionBellowPrimitive(this); }
        internal void Release(RenderFrame frame) { Manager.Release(this, frame); }
    }
    internal sealed class FlexibleConnectionBellowPrimitive : RenderPrimitive
    {
        readonly FlexibleConnectionBellowSnapshot Snapshot;
        internal FlexibleConnectionBellowPrimitive(FlexibleConnectionBellowSnapshot snapshot) { Snapshot = snapshot; }
        public override void Draw(GraphicsDevice device)
        {
            var c = Snapshot.Connection;
            if (c.Buffer == null || c.IndexBuffer == null) throw new InvalidOperationException("Bellows GPU lifecycle violation.");
            if (c.Uploaded != Snapshot.Version || c.Buffer.IsContentLost)
            { c.Buffer.SetData(Snapshot.Vertices, 0, Snapshot.Vertices.Length, SetDataOptions.Discard); c.Uploaded = Snapshot.Version; }
            var previous = device.RasterizerState;
            try
            {
                device.RasterizerState = RasterizerState.CullCounterClockwise;
                device.SetVertexBuffers(c.Bindings); device.Indices = c.IndexBuffer;
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, c.Mesh.Indices.Length / 3);
            }
            finally { device.RasterizerState = previous; }
        }
    }

    internal sealed class FlexibleConnectionBellowManager
    {
        readonly object Gate = new object();
        readonly List<FlexibleConnectionBellowOwner> Owners = new List<FlexibleConnectionBellowOwner>();
        bool Closed;
        internal void Register(FlexibleConnectionBellowOwner owner)
        { lock (Gate) { owner.Retired = Closed; if (!Closed) Owners.Add(owner); } }
        internal FlexibleConnectionBellowSnapshot Acquire(FlexibleConnectionBellowConnection c, RenderFrame frame)
        {
            lock (Gate)
            {
                if (Closed || c.Owner.Retired || c.First.Frame == frame || c.Second.Frame == frame) return null;
                var s = c.First.Frame == null && !c.First.Building ? c.First : c.Second.Frame == null && !c.Second.Building ? c.Second : null;
                if (s == null) return null;
                s.Building = true; s.Manager = this; c.Owner.References++; return s;
            }
        }
        internal bool Complete(FlexibleConnectionBellowSnapshot s, RenderFrame frame, bool valid)
        {
            lock (Gate)
            {
                s.Building = false;
                if (!valid || Closed || s.Connection.Owner.Retired) { s.Connection.Owner.References--; return false; }
                s.Frame = frame; s.Version = ++s.Connection.Version;
                frame.RegisterFlexibleConnectionBellowSnapshot(s); return true;
            }
        }
        internal void Release(FlexibleConnectionBellowSnapshot s, RenderFrame frame)
        { lock (Gate) { if (s.Frame != frame) return; s.Frame = null; s.Next = null; s.Connection.Owner.References--; } }
        internal void Retire(FlexibleConnectionBellowOwner owner) { lock (Gate) owner.Retired = true; }
        internal void Mark()
        { lock (Gate) foreach (var owner in Owners) foreach (var c in owner.Connections) c.Material.Mark(); }
        internal void ProcessPending(GraphicsDevice device)
        {
            while (true)
            {
                FlexibleConnectionBellowOwner pending = null; bool release = false;
                lock (Gate)
                {
                    if (Closed) return;
                    for (int i = 0; i < Owners.Count; i++)
                    {
                        var owner = Owners[i];
                        if (owner.Retired && owner.References == 0)
                        { Owners.RemoveAt(i); pending = owner; release = true; break; }
                        if (!owner.Prepared && (!owner.Retired || owner.References > 0))
                        { owner.Prepared = true; pending = owner; break; }
                    }
                }
                if (pending == null) return;
                if (release) foreach (var c in pending.Connections) c.Release();
                else
                {
                    try { foreach (var c in pending.Connections) c.Prepare(device); }
                    catch { foreach (var c in pending.Connections) c.Release(); throw; }
                }
            }
        }
        internal void Close()
        {
            lock (Gate) Closed = true;
            while (true)
            {
                FlexibleConnectionBellowOwner owner;
                lock (Gate)
                {
                    if (Owners.Count == 0) return;
                    int i = Owners.Count - 1; owner = Owners[i]; Owners.RemoveAt(i); owner.Retired = true;
                }
                foreach (var c in owner.Connections) c.Release();
            }
        }
    }
}
