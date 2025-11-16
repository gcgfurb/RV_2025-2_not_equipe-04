using UnityEngine;
using OpenCVForUnity.UnityIntegration.Helper.AR;

public class WordPrefab : ARGameObject
{
    [Header("Tamanho físico do marcador (m)")]
    public float markerLengthMeters = 0.055f;

    [Header("Corrigir rotação do modelo (OpenCV → Unity)")]
    public bool fixModelRotation = true;

    // Criados automaticamente
    [HideInInspector] public Transform rotationFix;
    [HideInInspector] public Transform modelRoot;
    [HideInInspector] public Transform modelInstance;

    void Awake()
    {
        // ===============================
        //   CRIA STRUCT IGUAL AO ARCUBE
        // ===============================

        // RotationFix (onde a rotação OpenCV vira correta)
        rotationFix = new GameObject("RotationFix").transform;
        rotationFix.SetParent(transform, false);
        rotationFix.localPosition = Vector3.zero;
        rotationFix.localRotation = Quaternion.identity;
        rotationFix.localScale = Vector3.one;

        // ModelRoot (onde o modelo 3D real vai ficar)
        modelRoot = new GameObject("ModelRoot").transform;
        modelRoot.SetParent(rotationFix, false);
        modelRoot.localPosition = Vector3.zero;
        modelRoot.localRotation = Quaternion.identity;
        modelRoot.localScale = Vector3.one;
    }

    // Carrega QUALQUER prefab do WordData
    public void InitializeModelRoot(Transform model)
    {
        // Remove modelo anterior, se existir
        foreach (Transform c in modelRoot)
            Destroy(c.gameObject);

        modelInstance = model;
        modelInstance.SetParent(modelRoot, false);

        // Reseta
        modelInstance.localPosition = Vector3.zero;
        modelInstance.localRotation = Quaternion.identity;
        modelInstance.localScale = Vector3.one;

        // Corrige rotação para OpenCV → Unity
        if (fixModelRotation)
            modelInstance.localRotation = Quaternion.Euler(-90, 0, 0);
    }

    public override void UpdateTransform(ARHelper helper)
    {
        base.UpdateTransform(helper);

        // Escala física igual exemplo do ARCube
        transform.localScale = Vector3.one * markerLengthMeters;
    }
}
