using Godot;

/// <summary>
/// Kinematic player character for Test/Game mode (§3.4). A hidden CharacterBody3D "body" is swept
/// through the lot with MoveAndSlide (gravity, floor detection, camera-relative WASD, Space to
/// jump) and its position is written back onto the visual capsule LotObject every physics step, so
/// the part the creator sees IS the character the player moves.
///
/// The body wears only the event-only layer bit (§3.7) — not any group layer, so no raycast or
/// pick can reach it — and masks the Character group's row of the §3.5 matrix, so which parts the
/// player collides with is creator-set. It excludes the character's own static collider, so the
/// part never blocks itself — the same technique PartDragController uses for direct part dragging.
/// Movement is deliberately kinematic (not a rigid body), matching the milestone's "basic movement".
/// </summary>
public sealed class CapsuleController
{
	private const float MoveSpeed = 9f;
	private const float JumpSpeed = 6f;
	private const float Gravity = 24f;
	private const float TerminalFall = -45f;

	/// <summary>Below this closing speed nothing is pushed, so resting/brushing contacts stay calm.</summary>
	private const float PushMinSpeed = 0.25f;

	/// <summary>Cap on the speed the push transfers; the controller's own MoveSpeed is only slightly
	/// higher, so a sprint cannot rocket a part.</summary>
	private const float PushMaxSpeed = 8f;

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

	/// <summary>True when <paramref name="node"/> is this player's invisible physics body. The
	/// event layer (§3.7) uses it to attribute a touch to the player's character entity.</summary>
	public bool OwnsBody(Node node)
	{
		return node != null && _body != null && GodotObject.IsInstanceValid(_body) && node == _body;
	}

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
		// No group layer, so the body is never a collision target or a pick hit; it wears only the
		// event-only bit (§3.7), which nothing but an event sensor looks for. The mask is the
		// Character group's row of the §3.5 matrix, so which parts the player collides with is
		// creator-set — the same split PartDragController.CreateMover uses.
		_body.CollisionLayer = LotCollisionGroups.CharacterBodyBit;
		_body.CollisionMask = LotCollisionGroups.MaskForName(LotCollisionGroups.CharacterGroup);
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

		Vector3 commanded = horizontal + Vector3.Up * _verticalVelocity;
		_body.Velocity = commanded;
		_body.MoveAndSlide();
		// The push reads the COMMANDED velocity, not _body.Velocity: move_and_slide rewrites
		// Velocity into the post-slide (blocked) value, so the closing speed would already read
		// zero exactly when the player is pressed against the body being pushed.
		PushCollidedBodies(commanded);

		// The visible part follows the swept body, so collision resolution (walls, slopes, the
		// ground) is what the creator sees.
		_character.GlobalPosition = _body.GlobalPosition;
	}

	/// <summary>
	/// Hands the player's motion into the dynamic bodies it ran into. A <c>CharacterBody3D</c> is
	/// kinematic, and the engine never transfers a kinematic body's movement into a
	/// <c>RigidBody3D</c> — without this, a loose part the player walks into behaves like a wall
	/// ("it freezes against the character"). Each slide collision with a rigid body gets the
	/// impulse that closes the speed gap along the contact normal, applied at the contact point
	/// (so a hinged part swings rather than sliding rigidly) and mass-scaled (light and heavy
	/// parts answer at the same pace — the Roblox feel).
	///
	/// The body is woken first: a sleeping body swallows impulses, and that alone reads as a
	/// freeze. Anchored parts are static bodies, so they are not pushed — by design.
	/// </summary>
	private void PushCollidedBodies(Vector3 commandedVelocity)
	{
		int count = _body.GetSlideCollisionCount();
		for (int i = 0; i < count; i++)
		{
			KinematicCollision3D collision = _body.GetSlideCollision(i);
			RigidBody3D pushed = collision.GetCollider() as RigidBody3D;
			if (pushed == null || !GodotObject.IsInstanceValid(pushed)) continue;

			Vector3 impulse;
			if (!TryComputePush(commandedVelocity, collision.GetNormal(), pushed.LinearVelocity,
				pushed.Mass, out impulse)) continue;

			pushed.Sleeping = false;
			pushed.ApplyImpulse(impulse, collision.GetPosition() - pushed.GlobalPosition);
		}
	}

	/// <summary>
	/// The impulse for one contact — pure, so the self-test can pin the arithmetic (the integral
	/// is <see cref="PushCollidedBodies"/>). False when there is no push to make: the player is not
	/// closing in on the surface, or the body is already keeping up along the normal. Closing the
	/// deficit rather than adding a fixed force is what stops a continued contact from
	/// accelerating the body without bound.
	/// </summary>
	public static bool TryComputePush(Vector3 playerVelocity, Vector3 contactNormal,
		Vector3 bodyVelocity, float bodyMass, out Vector3 impulse)
	{
		impulse = Vector3.Zero;
		Vector3 pushDirection = -contactNormal; // the normal faces the player; push goes the other way
		float closing = playerVelocity.Dot(pushDirection);
		if (closing <= PushMinSpeed) return false;

		float bodyAlong = bodyVelocity.Dot(pushDirection);
		float deficit = Mathf.Min(closing, PushMaxSpeed) - bodyAlong;
		if (deficit <= PushMinSpeed) return false;

		impulse = pushDirection * (deficit * bodyMass);
		return true;
	}
}
