using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuDificuldade : MonoBehaviour
{
    public void IniciarJogoFacil()
    {
        StartGame(Dificuldade.Facil);
    }

    public void IniciarJogoMedio()
    {
        StartGame(Dificuldade.Medio);
    }

    public void IniciarJogoDificil()
    {
        StartGame(Dificuldade.Dificil);
    }

    private void StartGame(Dificuldade dificuldade)
    {
        GameManager.Instance.DefinirDificuldade(dificuldade);
        SceneManager.LoadScene("GameScene");
    }
}
