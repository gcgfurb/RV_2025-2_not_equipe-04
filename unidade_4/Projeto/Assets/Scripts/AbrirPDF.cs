using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class AbrirPDF : MonoBehaviour
{
    [Header("Nome do arquivo dentro de StreamingAssets")]
    public string pdfFileName = "Letras_para_imprimir.pdf";

    // Chame este método no OnClick do botão
    public void OnAbrirPDFButton()
    {
        StartCoroutine(AbrirPDFCoroutine());
    }

    private IEnumerator AbrirPDFCoroutine()
    {
        string streamingPath = Path.Combine(Application.streamingAssetsPath, pdfFileName);

#if UNITY_ANDROID && !UNITY_EDITOR
        // No Android, StreamingAssets fica dentro do .apk, então copiamos para um caminho "normal"
        string destino = Path.Combine(Application.persistentDataPath, pdfFileName);

        if (!File.Exists(destino))
        {
            using (UnityWebRequest www = UnityWebRequest.Get(streamingPath))
            {
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("Erro ao copiar PDF: " + www.error);
                    yield break;
                }

                File.WriteAllBytes(destino, www.downloadHandler.data);
            }
        }

        // Abre o PDF com o aplicativo padrão do sistema (Google Drive, visualizador de PDF, etc.)
        Application.OpenURL(destino);
#else
        // No Editor, Windows, iOS, etc. funciona direto
        Application.OpenURL(streamingPath);
        yield break;
#endif
    }
}
