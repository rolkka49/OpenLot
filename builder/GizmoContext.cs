using Godot;

/// <summary>
/// Gizmo interaction state, ported from Im3d's Context (im3d.h:700-800, im3d.cpp:1838-1915).
///
/// One instance lives for the whole builder session: Im3d's hot/active/hotDepth state is
/// deliberately *not* reset per frame. The only ways a handle stops being hot are MakeHot()
/// stealing it (which requires a strictly smaller depth) or ResetId() when the hot handle stops
/// intersecting. That persistence is what makes hover feel stable instead of flickering, so it is
/// preserved exactly.
///
/// The Im3d pieces that do not apply to this port are omitted rather than stubbed: the vertex and
/// text buffers, layers, sorting, culling and the matrix/id stacks (drawing goes through ImGui and
/// the world transform comes from the Godot node), and the T/R/S/L hotkey mode switching (the
/// active tool comes from ToolboxPanel.CurrentGizmoMode, so there is a single source of truth).
/// </summary>
public sealed class GizmoContext
{
	/// <summary>Im3d Id_Invalid (0) — no handle.</summary>
	public const uint IdInvalid = 0;

	private readonly GizmoAppData _appData;

	// Gizmo ("app") level ids, i.e. which gizmo is hot/active as a whole.
	private uint _appId = IdInvalid;
	private uint _appActiveId = IdInvalid;
	private uint _appHotId = IdInvalid;
	private uint _appIdActivated = IdInvalid;
	private uint _previousAppId = IdInvalid;

	// Handle level ids.
	private uint _hotId = IdInvalid;
	private uint _activeId = IdInvalid;
	private float _hotDepth = float.MaxValue;

	// Drag bookkeeping (Im3d m_gizmoStateVec3 / m_gizmoStateFloat / m_gizmoStateMat3). Im3d
	// aliases the view-plane normal through m_gizmoStateMat3; a plain Vector3 field does the same
	// job without the aliasing.
	private Vector3 _gizmoStateVec3;
	private float _gizmoStateFloat;
	private Vector3 _storedViewNormal;
	private Basis _storedRotation;
	private Vector3 _storedScalePosition;

	// Key state captured during BeginFrame (Im3d m_keyDownCurr/m_keyDownPrev, im3d.h:783-784).
	private readonly bool[] _keyDownCurr = new bool[GizmoKeys.Count];
	private readonly bool[] _keyDownPrev = new bool[GizmoKeys.Count];

	/// <summary>Im3d default m_gizmoHeightPixels (im3d.cpp:2076).</summary>
	public float GizmoHeightPixels = 64.0f;

	/// <summary>Im3d default m_gizmoSizePixels (im3d.cpp:2077).</summary>
	public float GizmoSizePixels = 5.0f;

	public GizmoContext(GizmoAppData appData)
	{
		_appData = appData;
	}

	public GizmoAppData AppData { get { return _appData; } }

	public uint HotId { get { return _hotId; } }
	public uint ActiveId { get { return _activeId; } }
	public float HotDepth { get { return _hotDepth; } }
	public uint AppIdActivated { get { return _appIdActivated; } }

	/// <summary>Im3d m_appHotId — the gizmo that owns the current hot handle.</summary>
	public uint AppHotId { get { return _appHotId; } }

	/// <summary>Drag anchor / scratch value shared with the behaviors (Im3d m_gizmoStateVec3).</summary>
	public Vector3 GizmoStateVec3
	{
		get { return _gizmoStateVec3; }
		set { _gizmoStateVec3 = value; }
	}

	/// <summary>Im3d m_gizmoStateFloat.</summary>
	public float GizmoStateFloat
	{
		get { return _gizmoStateFloat; }
		set { _gizmoStateFloat = value; }
	}

	/// <summary>
	/// View-plane normal captured on view-plane drag start. Im3d stores this by aliasing
	/// m_gizmoStateMat3 (im3d.cpp:1009); a dedicated field is the same thing made readable.
	/// </summary>
	public Vector3 StoredViewNormal
	{
		get { return _storedViewNormal; }
		set { _storedViewNormal = value; }
	}

	/// <summary>
	/// Rotation captured when the rotation gizmo becomes active. Im3d keeps this in m_gizmoStateMat3
	/// (im3d.cpp:1093,1176); a dedicated Basis field is the same thing made readable, exactly as
	/// StoredViewNormal is for the translate gizmo's alias of the same slot.
	/// </summary>
	public Basis StoredRotation
	{
		get { return _storedRotation; }
		set { _storedRotation = value; }
	}

	/// <summary>
	/// View-plane point where a uniform-scale drag started. Im3d overlays a Vec3 on the first three
	/// floats of m_gizmoStateMat3 (im3d.cpp:1247); a dedicated field keeps that alias readable.
	/// </summary>
	public Vector3 StoredScalePosition
	{
		get { return _storedScalePosition; }
		set { _storedScalePosition = value; }
	}

	public bool IsHot(uint id) { return id != IdInvalid && id == _hotId; }
	public bool IsActive(uint id) { return id != IdInvalid && id == _activeId; }

	/// <summary>Im3d Context::isKeyDown — im3d.h:709.</summary>
	public bool IsKeyDown(int key) { return _keyDownCurr[key]; }

	/// <summary>Im3d Context::wasKeyPressed — im3d.h:710.</summary>
	public bool WasKeyPressed(int key) { return _keyDownCurr[key] && !_keyDownPrev[key]; }

	/// <summary>
	/// Im3d Context::reset() (im3d.cpp:1838-1915), reduced to the state this port reads. Called
	/// once per frame before the gizmo runs. Note what is *absent*: hot/active/hotDepth are left
	/// alone on purpose (they only change via MakeHot/MakeActive/ResetId), and appIdActivated is
	/// cleared at the end exactly as Im3d does at im3d.cpp:1914.
	/// </summary>
	public void BeginFrame()
	{
		_appData.ViewDirection = _appData.ViewDirection.Normalized();

		// "copy keydown array internally so that we can make a delta to detect key presses"
		System.Array.Copy(_keyDownCurr, _keyDownPrev, GizmoKeys.Count);
		System.Array.Copy(_appData.KeyDown, _keyDownCurr, GizmoKeys.Count);

		_appIdActivated = IdInvalid;
	}

	/// <summary>Im3d Context::makeHot — im3d.cpp:2896. Returns true if this call took hotness.</summary>
	public bool MakeHot(uint id, float depth, bool intersects)
	{
		if (_activeId == IdInvalid && depth < _hotDepth && intersects && !IsKeyDown(GizmoKeys.ActionSelect))
		{
			_hotId = id;
			_appHotId = _appId;
			_hotDepth = depth;

			return true;
		}

		return false;
	}

	/// <summary>Im3d Context::makeActive — im3d.cpp:2910.</summary>
	public void MakeActive(uint id)
	{
		_activeId = id;
		_appActiveId = id == IdInvalid ? IdInvalid : _appId;
		_appIdActivated = _appActiveId;
	}

	/// <summary>Im3d Context::resetId — im3d.cpp:2917. Clears every id and restores hotDepth.</summary>
	public void ResetId()
	{
		_activeId = _hotId = _appActiveId = _appHotId = _appIdActivated = IdInvalid;
		_hotDepth = float.MaxValue;
	}

	/// <summary>Im3d Context::pushId, narrowed to one gizmo at a time.</summary>
	public void BeginGizmo(uint id)
	{
		_previousAppId = _appId;
		_appId = id;
	}

	/// <summary>Im3d Context::popId.</summary>
	public void EndGizmo()
	{
		_appId = _previousAppId;
	}

	/// <summary>
	/// Im3d Context::pixelsToWorldSize — im3d.cpp:2380. Converts a pixel size at a world position
	/// into world units, so handles keep a constant on-screen size regardless of distance.
	/// </summary>
	public float PixelsToWorldSize(Vector3 position, float pixels)
	{
		float d = _appData.ProjectionOrtho ? 1.0f : (position - _appData.ViewOrigin).Length();
		return _appData.ProjectionScaleY * d * (pixels / _appData.ViewportSize.Y);
	}

	/// <summary>
	/// Im3d Context::worldSizeToPixels — im3d.cpp:2386. The inverse of PixelsToWorldSize, used to
	/// express a world-space radius (e.g. a hit-volume capsule) as an on-screen pixel size.
	/// </summary>
	public float WorldSizeToPixels(Vector3 position, float size)
	{
		float d = _appData.ProjectionOrtho ? 1.0f : (position - _appData.ViewOrigin).Length();
		if (d <= 0.0f)
		{
			return 0.0f;
		}
		return (size * _appData.ViewportSize.Y) / d / _appData.ProjectionScaleY;
	}

	/// <summary>
	/// Im3d Context::estimateLevelOfDetail — im3d.cpp:2392. Picks a tessellation count from the
	/// on-screen angular size of the primitive, so rings stay cheap when far away and smooth when
	/// close. Ortho has no distance falloff, so Im3d returns the max detail there.
	/// </summary>
	public int EstimateLevelOfDetail(Vector3 position, float worldSize, int min, int max)
	{
		if (_appData.ProjectionOrtho)
		{
			return max;
		}

		float d = (position - _appData.ViewOrigin).Length();
		float x = Mathf.Clamp(2.0f * Mathf.Atan(worldSize / (2.0f * d)), 0.0f, 1.0f);
		float fmin = min;
		float fmax = max;

		return (int)(fmin + (fmax - fmin) * x);
	}

	/// <summary>
	/// Builds a stable handle id. Im3d hashes the handle name together with the pushed id
	/// (im3d.cpp:839); the hot/active state machine only needs uniqueness within a gizmo and
	/// stability across frames, so the pair is packed instead of hashed — the result is
	/// deterministic and readable in a debugger. The gizmo id must be non-zero so that no handle
	/// can collide with IdInvalid.
	/// </summary>
	public static uint MakeHandleId(uint gizmoId, int index)
	{
		return (gizmoId << 8) | (uint)(index & 0xFF);
	}
}