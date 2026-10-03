using Godot;

/// <summary>
/// Kinematic player character for Test/Game mode (§3.4). A hidden CharacterBody3D "body" is swept
/// through the lot with MoveAndSlide (gravity, floor detection, camera-relative WASD, Space to
/// jump) and its position is written back onto the visual capsule LotObject every physics step, so
/// the part the creator sees IS the character the player moves.
///
/// The body is layer 0 / mask 1 and excludes the character's own static collider, so the part never
/// blocks itself — the same technique PartDragController uses for direct part dragging. Movement is
/// deliberately kinematic (not a rigid body), matching the milestone's "basic movement".
/// </summary>
public sealed class CapsuleController
{
	private const float MoveSpeed = 9f;
	private const float JumpSpeed = 6f;
	private const float Gravity = 24f;
	private const float TerminalFall = -45f;

	private readonly BuilderScene _scene;
	private CharacterBody3D _body;
	private LotObject _character;
	private float _verticalVelocity;
	private float _yaw;

	public CapsuleController(BuilderScene scene)
	{
		_scene = scene;
	}

	public LotObject Character { get { return _character; } }

	public bool IsAttached { get { return _body != null && GodotObject.IsInstanceValid(_body); } }

	/// <summary>Current body position (Vector3.Zero while nothing is attached).</summary>
	public Vector3 Position { get { return IsAttached ? _body.GlobalPosition : Vector3.Zero; } }

	public bool IsGrounded { get { return IsAttached && _body.IsOnFloor(); } }

	/// <summary>
	/// Attaches the controller to a lot capsule, creating the physics body inside the lot's physics
	/// space. Returns false when the capsule is not there.
	/// </summary>
	public bool Attach(LotObject character)
	{
		Detach();
		if (character == null || !GodotObject.IsInstanceValid(character)) return false;

		_character = character;

		_body = new CharacterBody3D();
		_body.Name = "PlayerBody";
		// Layer 0 so the body is never a collision target or a pick hit; mask 1 so it detects the
		// lot's parts — the same split PartDragController.CreateMover uses.
		_body.CollisionLayer = 0;
		_body.CollisionMask = 1;
		_body.SetMeta(LotObject.InternalChildMeta, true);

		CapsuleShape3D shape = new CapsuleShape3D();
		shape.Radius = 0.5f;
		shape.Height = 2.0f;
		CollisionShape3D bodyShape = new CollisionShape3D();
		bodyShape.Name = "Shape";
		bodyShape.Shape = shape;
		bodyShape.SetMeta(LotObject.InternalChildMeta, true);
		_body.AddChild(bodyShape);

		_scene.LotRoot.AddChild(_body);
		_body.GlobalPosition = character.GlobalPosition;
		_body.ForceUpdateTransform();

		// The visual part's static collider sits exactly where the body is: exclude it, or the body
		// starts inside itself and can never move.
		if (character.CollisionBody != null)
			_body.AddCollisionExceptionWith(character.CollisionBody);

		_verticalVelocity = 0f;
		return true;
	}

	/// <summary>
	/// Removes the physics body. The visual capsule is owned by the lot (a script spawned it), so it
	/// is left exactly where it is.
	/// </summary>
	public void Detach()
	{
		if (_body != null && GodotObject.IsInstanceValid(_body)) _body.QueueFree();
		_body = null;
		_character = null;
		_verticalVelocity = 0f;
	}

	public void SetYaw(float degrees)
	{
		_yaw = degrees;
	}

	/// <summary>
	/// One physics step of movement (kinematic moves are only legal during the physics step, so the
	/// owner calls this from BuilderScene._PhysicsProcess while a player session is active).
	/// </summary>
	public void Tick(double delta)
	{
		if (!IsAttached) return;
		if (_character == null || !GodotObject.IsInstanceValid(_character))
		{
			// The character was destroyed under us (a script re-run, a deletion): stop driving.
			Detach();
			return;
		}

		float dt = (float)delta;
		float yawRad = Mathf.DegToRad(_yaw);
		Vector3 forward = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
		Vector3 right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));

		Vector3 move = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.W)) move += forward;
		if (Input.IsPhysicalKeyPressed(Key.S)) move -= forward;
		if (Input.IsPhysicalKeyPressed(Key.D)) move += right;
		if (Input.IsPhysicalKeyPressed(Key.A)) move -= right;
		Vector3 horizontal = move.LengthSquared() > 0f ? move.Normalized() * MoveSpeed : Vector3.Zero;

		if (_body.IsOnFloor())
		{
			if (_verticalVelocity < 0f) _verticalVelocity = 0f;
			if (Input.IsPhysicalKeyPressed(Key.Space)) _verticalVelocity = JumpSpeed;
		}
		else
		{
			_verticalVelocity = Mathf.Max(_verticalVelocity - Gravity * dt, TerminalFall);
		}

		_body.Velocity = horizontal + Vector3.Up * _verticalVelocity;
		_body.MoveAndSlide();

		// The visible part follows the swept body, so collision resolution (walls, slopes, the
		// ground) is what the creator sees.
		_character.GlobalPosition = _body.GlobalPosition;
	}
}
