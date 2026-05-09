using System.Text;
using UnityEditor;
using UnityEngine;

public static class TransparentPetBlendShapeUtility
{
    private const string ModelPath = "Assets/TransparentPet/CustomModel/user_pet_model.fbx";

    [MenuItem("Transparent Pet/Dump Blend Shapes")]
    public static void DumpBlendShapes()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(ModelPath);
        if (assets == null || assets.Length == 0)
        {
            Debug.LogWarning("No custom model found at " + ModelPath + ". Put your own FBX there before dumping blend shapes.");
            return;
        }

        StringBuilder builder = new StringBuilder();
        builder.AppendLine("TransparentPet blend shapes:");
        foreach (Object asset in assets)
        {
            Mesh mesh = asset as Mesh;
            if (mesh == null || mesh.blendShapeCount <= 0)
            {
                continue;
            }

            builder.AppendLine(mesh.name + " (" + mesh.blendShapeCount + ")");
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                builder.Append("  ").Append(i).Append(": ").AppendLine(mesh.GetBlendShapeName(i));
            }
        }

        Debug.Log(builder.ToString());
    }
}
