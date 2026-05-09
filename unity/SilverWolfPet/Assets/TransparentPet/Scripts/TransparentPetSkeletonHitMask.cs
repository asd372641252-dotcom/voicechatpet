using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TransparentPetSkeletonHitMask : MonoBehaviour
{
    public Animator animator;
    public Camera targetCamera;
    public bool enabledHitMask = true;
    public float bodyRadiusPixels = 18f;
    public float headRadiusPixels = 22f;
    public float limbRadiusPixels = 8f;
    public float handFootRadiusPixels = 7f;
    public bool debugDraw;

    private Texture2D _debugTexture;

    private readonly HumanBodyBones[] _bones =
    {
        HumanBodyBones.Hips,
        HumanBodyBones.Spine,
        HumanBodyBones.Chest,
        HumanBodyBones.UpperChest,
        HumanBodyBones.Neck,
        HumanBodyBones.Head,
        HumanBodyBones.LeftShoulder,
        HumanBodyBones.LeftUpperArm,
        HumanBodyBones.LeftLowerArm,
        HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder,
        HumanBodyBones.RightUpperArm,
        HumanBodyBones.RightLowerArm,
        HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg,
        HumanBodyBones.LeftLowerLeg,
        HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg,
        HumanBodyBones.RightLowerLeg,
        HumanBodyBones.RightFoot
    };

    private readonly BoneSegment[] _segments =
    {
        new BoneSegment(HumanBodyBones.Hips, HumanBodyBones.Spine),
        new BoneSegment(HumanBodyBones.Spine, HumanBodyBones.Chest),
        new BoneSegment(HumanBodyBones.Chest, HumanBodyBones.UpperChest),
        new BoneSegment(HumanBodyBones.UpperChest, HumanBodyBones.Neck),
        new BoneSegment(HumanBodyBones.Chest, HumanBodyBones.Neck),
        new BoneSegment(HumanBodyBones.Neck, HumanBodyBones.Head),
        new BoneSegment(HumanBodyBones.Chest, HumanBodyBones.LeftShoulder),
        new BoneSegment(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm),
        new BoneSegment(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
        new BoneSegment(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
        new BoneSegment(HumanBodyBones.Chest, HumanBodyBones.RightShoulder),
        new BoneSegment(HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm),
        new BoneSegment(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
        new BoneSegment(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
        new BoneSegment(HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg),
        new BoneSegment(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg),
        new BoneSegment(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
        new BoneSegment(HumanBodyBones.Hips, HumanBodyBones.RightUpperLeg),
        new BoneSegment(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
        new BoneSegment(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)
    };

    private readonly BonePoint[] _projected = new BonePoint[32];

    public bool TryContainsScreenPoint(Vector2 screenPoint, out bool contains)
    {
        contains = false;
        if (!enabledHitMask || animator == null || targetCamera == null || !animator.isHuman)
        {
            return false;
        }

        int count = ProjectBones();
        if (count < 3)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            float radius = RadiusForBone(_projected[i].Bone);
            if ((screenPoint - _projected[i].ScreenPoint).sqrMagnitude <= radius * radius)
            {
                contains = true;
                return true;
            }
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            if (!TryGetProjectedPoint(_segments[i].A, count, out Vector2 a) ||
                !TryGetProjectedPoint(_segments[i].B, count, out Vector2 b))
            {
                continue;
            }

            float radius = Mathf.Max(RadiusForBone(_segments[i].A), RadiusForBone(_segments[i].B));
            if (DistanceSquaredToSegment(screenPoint, a, b) <= radius * radius)
            {
                contains = true;
                return true;
            }
        }

        return true;
    }

    private void OnGUI()
    {
        if (!debugDraw)
        {
            return;
        }

        int count = ProjectBones();
        if (count <= 0)
        {
            return;
        }

        if (_debugTexture == null)
        {
            _debugTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _debugTexture.SetPixel(0, 0, new Color(0.4f, 0.65f, 1f, 0.45f));
            _debugTexture.Apply();
        }

        Color previousColor = GUI.color;
        GUI.color = new Color(0.4f, 0.65f, 1f, 0.45f);
        for (int i = 0; i < count; i++)
        {
            float radius = Mathf.Max(5f, RadiusForBone(_projected[i].Bone) * 0.35f);
            Vector2 point = _projected[i].ScreenPoint;
            GUI.DrawTexture(new Rect(point.x - radius, Screen.height - point.y - radius, radius * 2f, radius * 2f), _debugTexture);
        }

        GUI.color = previousColor;
    }

    private int ProjectBones()
    {
        int count = 0;
        for (int i = 0; i < _bones.Length && count < _projected.Length; i++)
        {
            Transform bone = animator.GetBoneTransform(_bones[i]);
            if (bone == null)
            {
                continue;
            }

            Vector3 screenPoint = targetCamera.WorldToScreenPoint(bone.position);
            if (screenPoint.z <= targetCamera.nearClipPlane)
            {
                continue;
            }

            _projected[count++] = new BonePoint(_bones[i], new Vector2(screenPoint.x, screenPoint.y));
        }

        return count;
    }

    private bool TryGetProjectedPoint(HumanBodyBones bone, int count, out Vector2 screenPoint)
    {
        for (int i = 0; i < count; i++)
        {
            if (_projected[i].Bone == bone)
            {
                screenPoint = _projected[i].ScreenPoint;
                return true;
            }
        }

        screenPoint = Vector2.zero;
        return false;
    }

    private float RadiusForBone(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Head:
            case HumanBodyBones.Neck:
                return headRadiusPixels;
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightHand:
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.RightFoot:
                return handFootRadiusPixels;
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.RightShoulder:
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.RightUpperArm:
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.RightLowerArm:
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.RightUpperLeg:
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.RightLowerLeg:
                return limbRadiusPixels;
            default:
                return bodyRadiusPixels;
        }
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.sqrMagnitude;
        if (lengthSquared <= 0.000001f)
        {
            return (point - a).sqrMagnitude;
        }

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
        Vector2 closest = a + ab * t;
        return (point - closest).sqrMagnitude;
    }

    private readonly struct BoneSegment
    {
        public readonly HumanBodyBones A;
        public readonly HumanBodyBones B;

        public BoneSegment(HumanBodyBones a, HumanBodyBones b)
        {
            A = a;
            B = b;
        }
    }

    private readonly struct BonePoint
    {
        public readonly HumanBodyBones Bone;
        public readonly Vector2 ScreenPoint;

        public BonePoint(HumanBodyBones bone, Vector2 screenPoint)
        {
            Bone = bone;
            ScreenPoint = screenPoint;
        }
    }
}
