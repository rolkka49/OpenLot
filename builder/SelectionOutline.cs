using System.Collections.Generic;
using Godot;

/// <summary>
/// The build-mode selection highlight: a SCREEN-SPACE (2D) outline of the selected and dragged
/// objects, in the style of Roblox's selection highlight — a constant pixel-width ring hugging
/// each part's silhouette, drawn over the viewport image.
///
/// <para>Why not a 3D hull: the first cut grew an inverted copy of each part's mesh (a <c>Grow</c>
/// material with front-face culling) as a child node. That is real geometry in the lot world: its
/// thickness is world-space (so it fattens as a part gets closer or bigger), it parallaxes like an
/// object instead of reading as an overlay, and every selected part pays a second full mesh draw.
/// This is the replacement, and it keeps the old contract that the pass costs nothing while
/// nothing is selected.</para>
///
/// <para>How it works: the outlined objects are re-drawn into a private mask viewport (their own
/// meshes, flat state-coloured materials, transparent background), and a full-viewport
/// <see cref="ColorRect"/> inside the lot viewport runs a small shader that turns the mask's
/// silhouettes into a ring of <see cref="OutlineThicknessPixels"/> screen pixels straddling each
/// edge. Being screen-space, the ring is as thick on a huge part as on a tiny one, and it is not a
/// scene object: nothing is added to the lot world.</para>
///
/// <para>Deliberate behaviour change (matching Roblox's own highlight default, which draws on
/// top): the ring draws over the scene, so a selected part behind a wall is still outlined. The
/// old hull was depth-tested. Restoring occlusion means comparing the mask's depth against the
/// scene's (a depth-writing mask plus a screen quad reading <c>hint_depth_texture</c>) — left out
/// of this cut on purpose.</para>
///
/// <para>This is the project's first runtime shader, kept deliberately small and in one place
/// (the same kind of named exception as the <c>Label3D</c> borrow on decals): a silhouette ring
/// cannot come from ImGui's draw list, which has no per-object mask. It is a Tier-3-flavoured
/// presentation detail and nothing above it reads the shader — the rest of the builder only ever
/// sees <see cref="LotObject.OutlineState"/>.</para>
/// </summary>
public sealed class SelectionOutline
{
	/// <summary>Outline colour of a selected object. Matches the UI elements' selection tint so a
	/// 3D selection and a lot-UI selection read the same.</summary>
	public static readonly Color SelectedColor = new Color(0.18f, 0.52f, 0.89f, 1f);

	/// <summary>Outline colour of the object currently being dragged.</summary>
	public static readonly Color DraggedColor = new Color(0.35f, 0.72f, 1.0f, 1f);

	/// <summary>
	/// Outline width in SCREEN pixels — the one number that defines the look. The ring straddles
	/// the silhouette edge, so the visible band is about twice this.
	/// </summary>
	public const float OutlineThicknessPixels = 1.5f;

	/// <summary>
	/// The ring shader (the project's first runtime shader — see the class summary for why one is
	/// needed). It samples the mask viewport: a pixel on a silhouette (some neighbour filled, some
	/// not) becomes outline colour, everything else stays transparent. Colours come from the mask
	/// itself, so selected and dragged objects are coloured in the one pass.
	/// </summary>
	private const string RingShaderCode = @"
shader_type canvas_item;

uniform sampler2D mask_texture : filter_linear, repeat_disable;
uniform vec2 mask_size = vec2(1920.0, 1080.0);
uniform float thickness = 1.5;
uniform float opacity = 1.0;

// Accumulate one sample into the ring statistics: the lowest and highest silhouette alpha found
// around this pixel say whether the pixel sits on an edge, and the alpha-weighted colour carries
// the outline colour (selected blue vs dragged light blue) straight through.
void accumulate_ring(vec4 sample_colour, inout float lowest, inout float highest,
		inout vec3 colour_sum, inout float weight) {
	lowest = min(lowest, sample_colour.a);
	highest = max(highest, sample_colour.a);
	colour_sum += sample_colour.rgb * sample_colour.a;
	weight += sample_colour.a;
}

void fragment() {
	vec2 texel = thickness / mask_size;

	vec4 own = texture(mask_texture, SCREEN_UV);
	float lowest = own.a;
	float highest = own.a;
	vec3 colour_sum = own.rgb * own.a;
	float weight = own.a;

	// Eight neighbours at the outline radius (the axis and diagonal directions), so the band is
	// even around the silhouette instead of boxy.
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2( texel.x, 0.0)), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2(-texel.x, 0.0)), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2(0.0,  texel.y)), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2(0.0, -texel.y)), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2( texel.x,  texel.y) * 0.7071), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2(-texel.x,  texel.y) * 0.7071), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2( texel.x, -texel.y) * 0.7071), lowest, highest, colour_sum, weight);
	accumulate_ring(texture(mask_texture, SCREEN_UV + vec2(-texel.x, -texel.y) * 0.7071), lowest, highest, colour_sum, weight);

	// On the edge when filled and empty samples coexist; wholly inside or wholly outside is not.
	float edge = smoothstep(0.15, 0.85, highest) * smoothstep(0.85, 0.15, lowest);
	vec3 ring_colour = weight > 0.0 ? colour_sum / weight : vec3(1.0);
	COLOR = vec4(ring_colour, edge * opacity);
}
";
	private readonly Builder _builder;

	/// <summary>The objects the last refresh marked Selected or Dragged (the mask's sources).</summary>
	private readonly List<LotObject> _outlined = new List<LotObject>();
	/// <summary>One mask mesh per outlined object, in the same order as <see cref="_outlined"/>.</summary>
	private readonly List<MeshInstance3D> _masks = new List<MeshInstance3D>();

	private SubViewport _maskViewport;
	private Node3D _maskRoot;
	private Camera3D _maskCamera;
	private CanvasLayer _overlayLayer;
	private ColorRect _overlay;
	private ShaderMaterial _overlayMaterial;
	private StandardMaterial3D _selectedMaskMaterial;
	private StandardMaterial3D _draggedMaskMaterial;
	private bool _active;

	public SelectionOutline(Builder builder)
	{
		_builder = builder;
		_builder.Selection.OnSelectionChanged += Refresh;
		// The mask is synced from frame_pre_draw rather than _Process: the freecam (and the session
		// camera) move in their own _Process, and a child's _Process runs AFTER its parent's, so
		// syncing from the builder would leave the ring a frame behind the scene during camera
		// motion. frame_pre_draw runs after every node's processing and before the renderer draws,
		// which is exactly the moment the frame's final poses exist. Disconnected in Shutdown (a
		// stale static-signal subscription would outlive the scene).
		RenderingServer.FramePreDraw += SyncMask;
		BuildResources();
	}

	/// <summary>Number of mask meshes currently drawn (diagnostics / the self-test).</summary>
	public int MaskCount { get { return _masks.Count; } }

	/// <summary>True while at least one object is outlined.</summary>
	public bool IsActive { get { return _masks.Count > 0; } }

	/// <summary>The objects the last refresh marked (diagnostics / the self-test).</summary>
	public IReadOnlyList<LotObject> Outlined { get { return _outlined; } }

	/// <summary>
	/// Drops the selection-changed subscription and the frame hook, and frees the mask and overlay
	/// resources. Mirrors GizmoController.Shutdown.
	/// </summary>
	public void Shutdown()
	{
		RenderingServer.FramePreDraw -= SyncMask;
		_builder.Selection.OnSelectionChanged -= Refresh;
		ClearMasks();
		_outlined.Clear();
		if (_overlayLayer != null && GodotObject.IsInstanceValid(_overlayLayer)) _overlayLayer.QueueFree();
		if (_maskViewport != null && GodotObject.IsInstanceValid(_maskViewport)) _maskViewport.QueueFree();
		_overlayLayer = null;
		_overlay = null;
		_maskViewport = null;
		_maskRoot = null;
		_maskCamera = null;
		_overlayMaterial = null;
	}


	/// <summary>
	/// Re-evaluates every lot object's outline state and rebuilds the mask from the result. Called
	/// when the selection changes and when a drag starts or ends — the only moments the outlined
	/// set can change.
	/// </summary>
	public void Refresh()
	{
		Node3D dragged = _builder.Parts != null ? _builder.Parts.DraggedNode : null;
		_outlined.Clear();
		RefreshChildren(_builder.Scene.LotRoot, dragged);
		RebuildMasks();
	}

	/// <summary>Walks the lot tree so objects nested inside model groups are highlighted too.</summary>
	private void RefreshChildren(Node parent, Node3D dragged)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			LotObject lotObject = child as LotObject;
			if (lotObject != null)
			{
				if (child == dragged)
				{
					lotObject.SetOutline(LotOutlineState.Dragged);
				}
				else if (_builder.Selection.IsSelected(child))
				{
					lotObject.SetOutline(LotOutlineState.Selected);
				}
				else
				{
					lotObject.SetOutline(LotOutlineState.None);
				}
				if (lotObject.OutlineState != LotOutlineState.None) _outlined.Add(lotObject);
			}
			RefreshChildren(child, dragged);
		}
	}

	/// <summary>
	/// Builds one mask mesh per outlined object. Reuses each object's own mesh resource (no
	/// geometry is duplicated) and one flat material per state, so a rebuild allocates only the
	/// MeshInstance3D nodes and only ever runs when the selection or the drag changes.
	/// </summary>
	private void RebuildMasks()
	{
		ClearMasks();
		if (_maskRoot == null || _outlined.Count == 0) return;

		for (int i = 0; i < _outlined.Count; i++)
		{
			LotObject source = _outlined[i];
			if (source == null || !GodotObject.IsInstanceValid(source) || source.MeshInstance == null) continue;

			MeshInstance3D mask = new MeshInstance3D();
			mask.Name = "OutlineMask";
			mask.Mesh = source.MeshInstance.Mesh;
			mask.MaterialOverride = source.OutlineState == LotOutlineState.Dragged
				? DraggedMaskMaterial
				: SelectedMaskMaterial;
			// A mask carries colour only: it must never cast anything.
			mask.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			_maskRoot.AddChild(mask);
			mask.GlobalTransform = source.GlobalTransform;
			_masks.Add(mask);
		}
	}

	private void ClearMasks()
	{
		for (int i = 0; i < _masks.Count; i++)
		{
			MeshInstance3D mask = _masks[i];
			if (mask != null && GodotObject.IsInstanceValid(mask)) mask.QueueFree();
		}
		_masks.Clear();
	}


	/// <summary>
	/// Per-frame sync, from frame_pre_draw (see the constructor for why). Only the transforms and
	/// the camera move here — the mask set itself is rebuilt by <see cref="Refresh"/> — so an empty
	/// selection costs one branch and nothing else.
	/// </summary>
	private void SyncMask()
	{
		if (_maskViewport == null || !GodotObject.IsInstanceValid(_maskViewport)) return;

		if (_masks.Count == 0)
		{
			Deactivate();
			return;
		}

		SubViewport lotViewport = _builder.Scene != null ? _builder.Scene.LotViewport : null;
		Camera3D camera = lotViewport != null ? lotViewport.GetCamera3D() : null;
		if (camera == null || lotViewport.Size.X <= 0 || lotViewport.Size.Y <= 0)
		{
			Deactivate();
			return;
		}

		if (!_active)
		{
			_active = true;
			if (_overlay != null) _overlay.Visible = true;
			_maskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		}

		// The mask viewport mirrors the lot viewport's size (it supplies the shader's pixel scale),
		// and its camera mirrors whichever camera is current — the freecam in build mode, the
		// player camera in a session.
		Vector2I size = lotViewport.Size;
		if (_maskViewport.Size != size) _maskViewport.Size = size;
		if (_overlayMaterial != null) _overlayMaterial.SetShaderParameter("mask_size", new Vector2(size.X, size.Y));

		SyncCamera(camera);

		for (int i = 0; i < _masks.Count; i++)
		{
			MeshInstance3D mask = _masks[i];
			LotObject source = i < _outlined.Count ? _outlined[i] : null;
			if (mask == null || !GodotObject.IsInstanceValid(mask)) continue;
			if (source == null || !GodotObject.IsInstanceValid(source))
			{
				mask.Visible = false;
				continue;
			}
			// Follow the source mesh's visibility, so anything hidden from the scene (the
			// first-person character's own body, most importantly) is not outlined either.
			mask.Visible = source.MeshInstance != null
				&& GodotObject.IsInstanceValid(source.MeshInstance)
				&& source.MeshInstance.IsVisibleInTree();
			mask.GlobalTransform = source.GlobalTransform;
		}
	}

	/// <summary>
	/// Nothing to draw: hide the ring and stop rendering the mask viewport entirely. That switch is
	/// what keeps the whole pass free while nothing is selected.
	/// </summary>
	private void Deactivate()
	{
		if (!_active)
		{
			return;
		}
		_active = false;
		if (_overlay != null) _overlay.Visible = false;
		_maskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
	}

	/// <summary>Copies the current lot camera onto the mask camera, property by property so no
	/// camera state (projection, near/far, offsets) can silently differ from the scene.</summary>
	private void SyncCamera(Camera3D source)
	{
		if (_maskCamera == null || !GodotObject.IsInstanceValid(_maskCamera)) return;
		_maskCamera.GlobalTransform = source.GlobalTransform;
		_maskCamera.Projection = source.Projection;
		_maskCamera.Fov = source.Fov;
		_maskCamera.Size = source.Size;
		_maskCamera.Near = source.Near;
		_maskCamera.Far = source.Far;
		_maskCamera.KeepAspect = source.KeepAspect;
		_maskCamera.HOffset = source.HOffset;
		_maskCamera.VOffset = source.VOffset;
	}
	/// <summary>
	/// Creates the mask viewport, its camera and root, and the overlay that draws the ring over the

	/// lot viewport's image. Everything belongs to the builder scene (the mask viewport) or to the
	/// lot viewport (the overlay), so scene teardown owns their lifetime too.
	/// </summary>
	private void BuildResources()
	{
		BuilderScene scene = _builder.Scene;
		if (scene == null) return;

		_maskViewport = new SubViewport();
		_maskViewport.Name = "OutlineMaskViewport";
		_maskViewport.OwnWorld3D = true;
		_maskViewport.TransparentBg = true;
		// MSAA is what makes the ring smooth: without it the silhouette edge the shader measures
		// is a hard pixel staircase. The mask is a handful of flat meshes, so 2x is cheap.
		_maskViewport.Msaa3D = Viewport.Msaa.Msaa2X;
		_maskViewport.GuiDisableInput = true;
		// Nothing is drawn until something is outlined (see Deactivate).
		_maskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
		_maskViewport.Size = scene.LotViewport != null ? scene.LotViewport.Size : new Vector2I(960, 540);
		scene.AddChild(_maskViewport);

		_maskRoot = new Node3D();
		_maskRoot.Name = "OutlineMasks";
		_maskViewport.AddChild(_maskRoot);

		_maskCamera = new Camera3D();
		_maskCamera.Name = "OutlineMaskCamera";
		_maskCamera.Current = true;
		_maskViewport.AddChild(_maskCamera);

		_selectedMaskMaterial = MakeMaskMaterial(SelectedColor);
		_draggedMaskMaterial = MakeMaskMaterial(DraggedColor);

		Shader shader = new Shader();
		shader.Code = RingShaderCode;
		_overlayMaterial = new ShaderMaterial();
		_overlayMaterial.Shader = shader;
		_overlayMaterial.SetShaderParameter("mask_texture", _maskViewport.GetTexture());
		_overlayMaterial.SetShaderParameter("thickness", OutlineThicknessPixels);
		_overlayMaterial.SetShaderParameter("mask_size", new Vector2(_maskViewport.Size.X, _maskViewport.Size.Y));

		// The ring is drawn from a canvas layer INSIDE the lot viewport, so it composites into the
		// very texture the Viewport window blits — the ImGui image needs no change and the ring is
		// part of the world's own look. The rect ignores the mouse: it must never claim a click from
		// the viewport.
		_overlayLayer = new CanvasLayer();
		_overlayLayer.Name = "OutlineOverlay";
		_overlay = new ColorRect();
		_overlay.Name = "OutlineRing";
		_overlay.MouseFilter = Control.MouseFilterEnum.Ignore;
		_overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_overlay.Material = _overlayMaterial;
		_overlay.Visible = false;
		_overlayLayer.AddChild(_overlay);
		scene.LotViewport.AddChild(_overlayLayer);
	}

	private static StandardMaterial3D MakeMaskMaterial(Color color)
	{
		StandardMaterial3D material = new StandardMaterial3D();
		// Unshaded, because the mask viewport has no lights: the mesh's silhouette IS the data the
		// ring shader reads, and its colour is the outline colour.
		material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		material.AlbedoColor = color;
		material.DisableReceiveShadows = true;
		return material;
	}

	private StandardMaterial3D SelectedMaskMaterial { get { return _selectedMaskMaterial; } }

	private StandardMaterial3D DraggedMaskMaterial { get { return _draggedMaskMaterial; } }



}
