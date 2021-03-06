﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://whitecore-sim.org
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using OMV = OpenMetaverse;
using WhiteCore.Framework.Physics;
using WhiteCore.Framework.SceneInfo;
using WhiteCore.Framework.ConsoleFramework;
using WhiteCore.Framework.Utilities;

namespace WhiteCore.Region.Physics.BulletSPlugin
{
    public sealed class BSCharacter : BSPhysObject
    {
        private static readonly string LogHeader = "[BULLETS CHAR]";

        // private bool _stopped;
        private OMV.Vector3 _size;
        private bool _selected;
        private OMV.Vector3 _position;
        private float _mass;
        private float _avatarVolume;
        private float _collisionScore;
        private OMV.Vector3 _acceleration;
        private OMV.Quaternion _orientation;
        private bool _flying;
        private bool _setAlwaysRun;
        private bool _throttleUpdates;
        private bool _floatOnWater;
        private OMV.Vector3 _rotationalVelocity;
        private float _buoyancy;
        private BSActorAvatarMove m_moveActor;
        private const string AvatarMoveActorName = "BSCharacter.AvatarMove";

        public BSCharacter(uint localID, String avName, BSScene parent_scene, OMV.Vector3 pos, OMV.Vector3 size,
            bool isFlying)
            : base(parent_scene, localID, avName, "BSCharacter")
        {
            _position = pos;

            _flying = isFlying;
            _orientation = OMV.Quaternion.Identity;
            RawVelocity = OMV.Vector3.Zero;
            _buoyancy = ComputeBuoyancyFromFlying(isFlying);
            Friction = BSParam.AvatarStandingFriction;
            Density = BSParam.AvatarDensity / BSParam.DensityScaleFactor;

            // Old versions of ScenePresence passed only the height. If width and/or depth are zero,
            //     replace with the default values.
            _size = size;
            if (_size.X == 0f) _size.X = BSParam.AvatarCapsuleDepth;
            if (_size.Y == 0f) _size.Y = BSParam.AvatarCapsuleWidth;

            // The dimensions of the physical capsule are kept in the scale.
            // Physics creates a unit capsule which is scaled by the physics engine.
            Scale = ComputeAvatarScale(_size);
            // set _avatarVolume and _mass based on capsule size, _density and Scale
            ComputeAvatarVolumeAndMass();

            // The avatar's movement is controlled by this motor that speeds up and slows down
            //    the avatar seeking to reach the motor's target speed.
            // This motor runs as a prestep action for the avatar so it will keep the avatar
            //    standing as well as moving. Destruction of the avatar will destroy the pre-step action.
            m_moveActor = new BSActorAvatarMove(PhysicsScene, this, AvatarMoveActorName);
            PhysicalActors.Add(AvatarMoveActorName, m_moveActor);

            DetailLog("{0},BSCharacter.create,call,size={1},scale={2},density={3},volume={4},mass={5}",
                LocalID, _size, Scale, Density, _avatarVolume, RawMass);

            // do actual creation in taint time
            PhysicsScene.TaintedObject("BSCharacter.create", delegate()
            {
                DetailLog("{0},BSCharacter.create,taint", LocalID);
                // New body and shape into PhysBody and PhysShape
                PhysicsScene.Shapes.GetBodyAndShape(true, PhysicsScene.World, this);

                SetPhysicalProperties();

                SubscribeEvents(1000);
            });
            return;
        }

        // called when this character is being destroyed and the resources should be released
        public override void Destroy()
        {
            base.Destroy();

            DetailLog("{0},BSCharacter.Destroy", LocalID);
            PhysicsScene.TaintedObject("BSCharacter.destroy", delegate()
            {
                PhysicsScene.Shapes.DereferenceBody(PhysBody, null /* bodyCallback */);
                PhysBody.Clear();
                PhysicsScene.Shapes.DereferenceShape(PhysShape, null /* bodyCallback */);
                PhysShape.Clear();
            });
        }

        private void SetPhysicalProperties()
        {
            PhysicsScene.PE.RemoveObjectFromWorld(PhysicsScene.World, PhysBody);

            ZeroMotion(true);
            ForcePosition = _position;

            // Set the velocity
            if (m_moveActor != null)
                m_moveActor.SetVelocityAndTarget(RawVelocity, RawVelocity, false, 0);

            ForceVelocity = RawVelocity;

            // This will enable or disable the flying buoyancy of the avatar.
            // Needs to be reset especially when an avatar is recreated after crossing a region boundry.
            Flying = _flying;

            PhysicsScene.PE.SetRestitution(PhysBody, BSParam.AvatarRestitution);
            PhysicsScene.PE.SetMargin(PhysShape, PhysicsScene.Params.collisionMargin);
            PhysicsScene.PE.SetLocalScaling(PhysShape, Scale);
            PhysicsScene.PE.SetContactProcessingThreshold(PhysBody, BSParam.ContactProcessingThreshold);
            if (BSParam.CcdMotionThreshold > 0f)
            {
                PhysicsScene.PE.SetCcdMotionThreshold(PhysBody, BSParam.CcdMotionThreshold);
                PhysicsScene.PE.SetCcdSweptSphereRadius(PhysBody, BSParam.CcdSweptSphereRadius);
            }

            UpdatePhysicalMassProperties(RawMass, false);

            // Make so capsule does not fall over
            PhysicsScene.PE.SetAngularFactorV(PhysBody, OMV.Vector3.Zero);

            // The avatar mover sets some parameters.
            PhysicalActors.Refresh();

            PhysicsScene.PE.AddToCollisionFlags(PhysBody, CollisionFlags.CF_CHARACTER_OBJECT);

            PhysicsScene.PE.AddObjectToWorld(PhysicsScene.World, PhysBody);

            // PhysicsScene.PE.ForceActivationState(PhysBody, ActivationState.ACTIVE_TAG);
            PhysicsScene.PE.ForceActivationState(PhysBody, ActivationState.DISABLE_DEACTIVATION);
            PhysicsScene.PE.UpdateSingleAabb(PhysicsScene.World, PhysBody);

            // Do this after the object has been added to the world
            PhysBody.collisionType = CollisionType.Avatar;
            PhysBody.ApplyCollisionMask(PhysicsScene);
        }

        public override void RequestPhysicsterseUpdate()
        {
            base.RequestPhysicsterseUpdate();
        }

        public override OMV.Vector3 Size
        {
            get
            {
                // Avatar capsule size is kept in the scale parameter.
                return _size;
            }

            set
            {
                _size = value;
                // Old versions of ScenePresence passed only the height. If width and/or depth are zero,
                //     replace with the default values.
                if (_size.X == 0f) _size.X = BSParam.AvatarCapsuleDepth;
                if (_size.Y == 0f) _size.Y = BSParam.AvatarCapsuleWidth;

                Scale = ComputeAvatarScale(_size);
                ComputeAvatarVolumeAndMass();
                DetailLog("{0},BSCharacter.setSize,call,size={1},scale={2},density={3},volume={4},mass={5}",
                    LocalID, _size, Scale, Density, _avatarVolume, RawMass);

                PhysicsScene.TaintedObject("BSCharacter.setSize", delegate()
                {
                    if (PhysBody.HasPhysicalBody && PhysShape.HasPhysicalShape)
                    {
                        PhysicsScene.PE.SetLocalScaling(PhysShape, Scale);
                        UpdatePhysicalMassProperties(RawMass, true);
                        // Make sure this change appears as a property update event
                        PhysicsScene.PE.PushUpdate(PhysBody);
                    }
                });
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set { BaseShape = value; }
        }

        // I want the physics engine to make an avatar capsule
        public override BSPhysicsShapeType PreferredPhysicalShape
        {
            get { return BSPhysicsShapeType.SHAPE_CAPSULE; }
        }

        public override bool Selected
        {
            set { _selected = value; }
        }

        public override bool IsSelected
        {
            get { return _selected; }
        }

        public override void CrossingFailure()
        {
            return;
        }

        public override void link(PhysicsActor obj)
        {
            return;
        }

        public override void delink()
        {
            return;
        }

        // Set motion values to zero.
        // Do it to the properties so the values get set in the physics engine.
        // Push the setting of the values to the viewer.
        // Called at taint time!
        public override void ZeroMotion(bool inTaintTime)
        {
            RawVelocity = OMV.Vector3.Zero;
            _acceleration = OMV.Vector3.Zero;
            _rotationalVelocity = OMV.Vector3.Zero;

            // Zero some other properties directly into the physics engine
            PhysicsScene.TaintedObject(inTaintTime, "BSCharacter.ZeroMotion", delegate()
            {
                if (PhysBody.HasPhysicalBody)
                    PhysicsScene.PE.ClearAllForces(PhysBody);
            });
        }

        public override void ZeroAngularMotion(bool inTaintTime)
        {
            _rotationalVelocity = OMV.Vector3.Zero;

            PhysicsScene.TaintedObject(inTaintTime, "BSCharacter.ZeroMotion", delegate()
            {
                if (PhysBody.HasPhysicalBody)
                {
                    PhysicsScene.PE.SetInterpolationAngularVelocity(PhysBody, OMV.Vector3.Zero);
                    PhysicsScene.PE.SetAngularVelocity(PhysBody, OMV.Vector3.Zero);
                    // The next also get rid of applied linear force but the linear velocity is untouched.
                    PhysicsScene.PE.ClearForces(PhysBody);
                }
            });
        }


        public override void LockAngularMotion(OMV.Vector3 axis)
        {
            return;
        }

        public override OMV.Vector3 RawPosition
        {
            get { return _position; }
            set { _position = value; }
        }

        public override OMV.Vector3 Position
        {
            get
            {
                // Don't refetch the position because this function is called a zillion times
                // _position = PhysicsScene.PE.GetObjectPosition(Scene.World, LocalID);
                return _position;
            }
            set
            {
                _position = value;

                PhysicsScene.TaintedObject("BSCharacter.setPosition", delegate()
                {
                    DetailLog("{0},BSCharacter.SetPosition,taint,pos={1},orient={2}", LocalID, _position, _orientation);
                    PositionSanityCheck();
                    ForcePosition = _position;
                });
            }
        }

        public override OMV.Vector3 ForcePosition
        {
            get
            {
                _position = PhysicsScene.PE.GetPosition(PhysBody);
                return _position;
            }
            set
            {
                _position = value;
                if (PhysBody.HasPhysicalBody)
                {
                    PhysicsScene.PE.SetTranslation(PhysBody, _position, _orientation);
                }
            }
        }


        // Check that the current position is sane and, if not, modify the position to make it so.
        // Check for being below terrain or on water.
        // Returns 'true' of the position was made sane by some action.
        private bool PositionSanityCheck()
        {
            bool ret = false;

            // TODO: check for out of bounds
            if (!PhysicsScene.TerrainManager.IsWithinKnownTerrain(RawPosition))
            {
                // The character is out of the known/simulated area.
                // Force the avatar position to be within known. ScenePresence will use the position
                //    plus the velocity to decide if the avatar is moving out of the region.
                RawPosition = PhysicsScene.TerrainManager.ClampPositionIntoKnownTerrain(RawPosition);
                DetailLog("{0},BSCharacter.PositionSanityCheck,notWithinKnownTerrain,clampedPos={1}", LocalID,
                    RawPosition);
                return true;
            }

            // If below the ground, move the avatar up
            float terrainHeight = PhysicsScene.TerrainManager.GetTerrainHeightAtXYZ(RawPosition);
            if (Position.Z < terrainHeight)
            {
                DetailLog("{0},BSCharacter.PositionSanityCheck,adjustForUnderGround,pos={1},terrain={2}", LocalID,
                    _position, terrainHeight);
                _position.Z = terrainHeight + BSParam.AvatarBelowGroundUpCorrectionMeters;
                ret = true;
            }
            if ((CurrentCollisionFlags & CollisionFlags.BS_FLOATS_ON_WATER) != 0)
            {
                float waterHeight = PhysicsScene.TerrainManager.GetWaterLevelAtXYZ(_position);
                if (Position.Z < waterHeight)
                {
                    _position.Z = waterHeight;
                    ret = true;
                }
            }

            return ret;
        }

        // A version of the sanity check that also makes sure a new position value is
        //    pushed back to the physics engine. This routine would be used by anyone
        //    who is not already pushing the value.
        private bool PositionSanityCheck(bool inTaintTime)
        {
            bool ret = false;
            if (PositionSanityCheck())
            {
                // The new position value must be pushed into the physics engine but we can't
                //    just assign to "Position" because of potential call loops.
                PhysicsScene.TaintedObject(inTaintTime, "BSCharacter.PositionSanityCheck", delegate()
                {
                    DetailLog("{0},BSCharacter.PositionSanityCheck,taint,pos={1},orient={2}", LocalID, _position,
                        _orientation);
                    ForcePosition = _position;
                });
                ret = true;
            }
            return ret;
        }

        public override float Mass
        {
            get { return _mass; }
        }

        // used when we only want this prim's mass and not the linkset thing
        public override float RawMass
        {
            get { return _mass; }
        }

        public override void UpdatePhysicalMassProperties(float physMass, bool inWorld)
        {
            OMV.Vector3 localInertia = PhysicsScene.PE.CalculateLocalInertia(PhysShape, physMass);
            PhysicsScene.PE.SetMassProps(PhysBody, physMass, localInertia);
        }

        public override OMV.Vector3 Force
        {
            get { return RawForce; }
            set
            {
                RawForce = value;
                // MainConsole.Instance.DebugFormat("{0}: Force = {1}", LogHeader, _force);
                PhysicsScene.TaintedObject("BSCharacter.SetForce", delegate()
                {
                    DetailLog("{0},BSCharacter.setForce,taint,force={1}", LocalID, RawForce);
                    if (PhysBody.HasPhysicalBody)
                        PhysicsScene.PE.SetObjectForce(PhysBody, RawForce);
                });
            }
        }

        // Avatars don't do vehicles
        public override int VehicleType
        {
            get { return (int)Vehicle.TYPE_NONE; }
            set { return; }
        }

        public override void VehicleFloatParam(int param, float value)
        {
        }

        public void VehicleVectorParam(int param, float value)
        {
        }

        public override void VehicleRotationParam(int param, OMV.Quaternion rotation)
        {
        }

        public override void VehicleFlags(int param, bool remove)
        {
        }

        public override OMV.Vector3 CenterOfMass
        {
            get { return OMV.Vector3.Zero; }
        }

        // Sets the target in the motor. This starts the changing of the avatar's velocity.
        public override OMV.Vector3 TargetVelocity
        {
            get { return base.m_targetVelocity; }
            set
            {
                DetailLog("{0},BSCharacter.setTargetVelocity,call,vel={1}", LocalID, value);
                OMV.Vector3 targetVel = value;
                targetVel *= 3.84f;
                if (_setAlwaysRun)
                    targetVel *= new OMV.Vector3(BSParam.AvatarAlwaysRunFactor, BSParam.AvatarAlwaysRunFactor, 1f);
                if (_flying)
                    targetVel *= 4f;

                m_targetVelocity = targetVel * (1f / PhysicsScene.TimeDilation);

                if (m_moveActor != null)
                    m_moveActor.SetVelocityAndTarget(RawVelocity, m_targetVelocity, false /* inTaintTime */, 3);
            }
        }

        // Directly setting velocity means this is what the user really wants now.
        public override OMV.Vector3 Velocity
        {
            get { return RawVelocity; }
            set
            {
                RawVelocity = value;
                // MainConsole.Instance.DebugFormat("{0}: set velocity = {1}", LogHeader, RawVelocity);
                PhysicsScene.TaintedObject("BSCharacter.setVelocity", delegate()
                {
                    if (m_moveActor != null)
                        m_moveActor.SetVelocityAndTarget(RawVelocity, RawVelocity, true /* inTaintTime */, 3);

                    DetailLog("{0},BSCharacter.setVelocity,taint,vel={1}", LocalID, RawVelocity);
                    ForceVelocity = RawVelocity;
                });
            }
        }

        public override OMV.Vector3 ForceVelocity
        {
            get { return RawVelocity; }
            set
            {
                PhysicsScene.AssertInTaintTime("BSCharacter.ForceVelocity");

                RawVelocity = value;
                PhysicsScene.PE.SetLinearVelocity(PhysBody, RawVelocity);
                PhysicsScene.PE.Activate(PhysBody, true);
            }
        }

        public override OMV.Vector3 Torque
        {
            get { return RawTorque; }
            set { RawTorque = value; }
        }

        public override float CollisionScore
        {
            get { return _collisionScore; }
            set { _collisionScore = value; }
        }

        public override OMV.Vector3 Acceleration
        {
            get { return _acceleration; }
            set { _acceleration = value; }
        }

        public override OMV.Quaternion RawOrientation
        {
            get { return _orientation; }
            set { _orientation = value; }
        }

        public override OMV.Quaternion Orientation
        {
            get { return _orientation; }
            set
            {
                // Orientation is set zillions of times when an avatar is walking. It's like
                //      the viewer doesn't trust us.
                if (_orientation != value)
                {
                    _orientation = value;
                    PhysicsScene.TaintedObject("BSCharacter.setOrientation",
                        delegate() { ForceOrientation = _orientation; });
                }
            }
        }

        // Go directly to Bullet to get/set the value.
        public override OMV.Quaternion ForceOrientation
        {
            get
            {
                _orientation = PhysicsScene.PE.GetOrientation(PhysBody);
                return _orientation;
            }
            set
            {
                _orientation = value;
                if (PhysBody.HasPhysicalBody)
                {
                    // _position = PhysicsScene.PE.GetPosition(BSBody);
                    PhysicsScene.PE.SetTranslation(PhysBody, _position, _orientation);
                }
            }
        }

        public override int PhysicsActorType
        {
            get { return (int)ActorTypes.Agent; }
        }

        public override bool IsPhysical
        {
            get { return true; }
            set { }
        }

        public override bool IsSolid
        {
            get { return true; }
        }

        public override bool IsStatic
        {
            get { return false; }
        }

        public override bool IsPhysicallyActive
        {
            get { return true; }
        }

        public override bool Flying
        {
            get { return _flying; }
            set
            {
                _flying = value;

                // simulate flying by changing the effect of gravity
                Buoyancy = ComputeBuoyancyFromFlying(_flying);
            }
        }

        // Flying is implimented by changing the avatar's buoyancy.
        // Would this be done better with a vehicle type?
        private float ComputeBuoyancyFromFlying(bool ifFlying)
        {
            return ifFlying ? 1f : 0f;
        }

        public override bool
            SetAlwaysRun
        {
            get { return _setAlwaysRun; }
            set { _setAlwaysRun = value; }
        }

        public override bool ThrottleUpdates
        {
            get { return _throttleUpdates; }
            set { _throttleUpdates = value; }
        }

        public override bool FloatOnWater
        {
            set
            {
                _floatOnWater = value;
                PhysicsScene.TaintedObject("BSCharacter.setFloatOnWater", delegate()
                {
                    if (PhysBody.HasPhysicalBody)
                    {
                        if (_floatOnWater)
                            CurrentCollisionFlags = PhysicsScene.PE.AddToCollisionFlags(PhysBody,
                                CollisionFlags.BS_FLOATS_ON_WATER);
                        else
                            CurrentCollisionFlags = PhysicsScene.PE.RemoveFromCollisionFlags(PhysBody,
                                CollisionFlags.BS_FLOATS_ON_WATER);
                    }
                });
            }
        }

        public override OMV.Vector3 RotationalVelocity
        {
            get { return _rotationalVelocity; }
            set { _rotationalVelocity = value; }
        }

        public override OMV.Vector3 ForceRotationalVelocity
        {
            get { return _rotationalVelocity; }
            set { _rotationalVelocity = value; }
        }

        // neg=fall quickly, 0=1g, 1=0g, pos=float up
        public override float Buoyancy
        {
            get { return _buoyancy; }
            set
            {
                _buoyancy = value;
                PhysicsScene.TaintedObject("BSCharacter.setBuoyancy", delegate()
                {
                    DetailLog("{0},BSCharacter.setBuoyancy,taint,buoy={1}", LocalID, _buoyancy);
                    ForceBuoyancy = _buoyancy;
                });
            }
        }

        public override float ForceBuoyancy
        {
            get { return _buoyancy; }
            set
            {
                PhysicsScene.AssertInTaintTime("BSCharacter.ForceBuoyancy");

                _buoyancy = value;
                DetailLog("{0},BSCharacter.setForceBuoyancy,taint,buoy={1}", LocalID, _buoyancy);
                // Buoyancy is faked by changing the gravity applied to the object
                float grav = BSParam.Gravity * (1f - _buoyancy);
                Gravity = new OMV.Vector3(0f, 0f, grav);
                if (PhysBody.HasPhysicalBody)
                    PhysicsScene.PE.SetGravity(PhysBody, Gravity);
            }
        }

        public override void AddForce(OMV.Vector3 force, bool pushforce)
        {
            // Since this force is being applied in only one step, make this a force per second.
            OMV.Vector3 addForce = force / PhysicsScene.LastTimeStep;
            AddForce(addForce, pushforce, false);
        }

        private void AddForce(OMV.Vector3 force, bool pushforce, bool inTaintTime)
        {
            if (force.IsFinite())
            {
                OMV.Vector3 addForce = Util.ClampV(force, BSParam.MaxAddForceMagnitude);
                // DetailLog("{0},BSCharacter.addForce,call,force={1}", LocalID, addForce);

                PhysicsScene.TaintedObject(inTaintTime, "BSCharacter.AddForce", delegate()
                {
                    // Bullet adds this central force to the total force for this tick
                    // DetailLog("{0},BSCharacter.addForce,taint,force={1}", LocalID, addForce);
                    if (PhysBody.HasPhysicalBody)
                    {
                        PhysicsScene.PE.ApplyCentralForce(PhysBody, addForce);
                    }
                });
            }
            else
            {
                MainConsole.Instance.WarnFormat("{0}: Got a NaN force applied to a character. LocalID={1}", LogHeader,
                    LocalID);
                return;
            }
        }

        public override void AddAngularForce(OMV.Vector3 force, bool pushforce, bool inTaintTime)
        {
        }

        private OMV.Vector3 ComputeAvatarScale(OMV.Vector3 size)
        {
            OMV.Vector3 newScale;

            // Bullet's capsule total height is the "passed height + radius * 2";
            // The base capsule is 1 diameter and 2 height (passed radius=0.5, passed height = 1)
            // The number we pass in for 'scaling' is the multiplier to get that base
            //     shape to be the size desired.
            // So, when creating the scale for the avatar height, we take the passed height
            //     (size.Z) and remove the caps.
            // Another oddity of the Bullet capsule implementation is that it presumes the Y
            //     dimension is the radius of the capsule. Even though some of the code allows
            //     for a asymmetrical capsule, other parts of the code presume it is cylindrical.

            // Scale is multiplier of radius with one of "0.5"
            newScale.X = size.X / 2f;
            newScale.Y = size.Y / 2f;

            // The total scale height is the central cylindar plus the caps on the two ends.
            newScale.Z = (size.Z + (Math.Min(size.X, size.Y) * 2)) / 2f;
            // If smaller than the endcaps, just fake like we're almost that small
            if (newScale.Z < 0)
                newScale.Z = 0.1f;

            return newScale;
        }

        // set _avatarVolume and _mass based on capsule size, _density and Scale
        private void ComputeAvatarVolumeAndMass()
        {
            _avatarVolume = (float)(
                Math.PI
                * Size.X / 2f
                * Size.Y / 2f // the area of capsule cylinder
                * Size.Z // times height of capsule cylinder
                + 1.33333333f
                * Math.PI
                * Size.X / 2f
                * Math.Min(Size.X, Size.Y) / 2
                * Size.Y / 2f // plus the volume of the capsule end caps
                );
            _mass = Density * BSParam.DensityScaleFactor * _avatarVolume;
        }

        // The physics engine says that properties have updated. Update same and inform
        // the world that things have changed.
        public override void UpdateProperties(EntityProperties entprop)
        {
            // Don't change position if standing on a stationary object.
            if (!IsStationary)
                _position = entprop.Position;

            _orientation = entprop.Rotation;

            if (entprop.Velocity != OMV.Vector3.Zero && entprop.Velocity.ApproxEquals(OMV.Vector3.Zero, 0.01f) &&
                Velocity != OMV.Vector3.Zero)
            {
                entprop.Velocity = OMV.Vector3.Zero;
                entprop.Acceleration = OMV.Vector3.Zero;
                entprop.RotationalVelocity = OMV.Vector3.Zero;
                Velocity = OMV.Vector3.Zero;

                TriggerSignificantMovement();
                TriggerMovementUpdate();
            }

            if (!entprop.Velocity.ApproxEquals(RawVelocity, 0.4f))
            {
                RawVelocity = entprop.Velocity;

                TriggerSignificantMovement();
                TriggerMovementUpdate();
            }

            _acceleration = entprop.Acceleration;
            _rotationalVelocity = entprop.RotationalVelocity;

            // Do some sanity checking for the avatar. Make sure it's above ground and inbounds.
            if (PositionSanityCheck(true))
            {
                DetailLog("{0},BSCharacter.UpdateProperties,updatePosForSanity,pos={1}", LocalID, _position);
                entprop.Position = _position;
            }

            // remember the current and last set values
            LastEntityProperties = CurrentEntityProperties;
            CurrentEntityProperties = entprop;

            // Tell the linkset about value changes
            // Linkset.UpdateProperties(UpdatedProperties.EntPropUpdates, this);

            // Avatars don't report their changes the usual way. Changes are checked for in the heartbeat loop.
            // base.RequestPhysicsterseUpdate();

            DetailLog("{0},BSCharacter.UpdateProperties,call,pos={1},orient={2},vel={3},accel={4},rotVel={5}",
                LocalID, _position, _orientation, RawVelocity, _acceleration, _rotationalVelocity);
        }
    }
}