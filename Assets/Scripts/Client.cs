using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Unity.Networking.Transport;
using Unity.FPS.AI;
using PlayFab;
using PlayFab.MultiplayerModels;
using PlayFab.ClientModels;

public class Client : MonoBehaviour
{
    public bool RunLocal;
    private NetworkDriver networkDriver;
    private NetworkConnection networkConnection;
    private bool isDone;
    private EnemyManager enemyManager;
    private GameObject[] enemies;
    private byte[] enemyStatus;
    private bool startedConnectionRequest = false;
    private bool isConnected = false;

    static public string matchAddress = "";
    static public ushort matchPort = 0;

    // private void RequestMultiplayerServer()
    // {
    //     RequestMultiplayerServerRequest requestData = new RequestMultiplayerServerRequest();
    //     requestData.BuildId = ""; // Build ID from the Multiplayer Dashboard
    //     requestData.PreferredRegions = new List<string>() { "EastUs" };
    //     requestData.SessionId = System.Guid.NewGuid().ToString(); // Generate a Session ID
    //     PlayFabMultiplayerAPI.RequestMultiplayerServer(requestData, OnRequestMultiplayerServer, OnRequestMultiplayerServerError);
    // }

    private void OnRequestMultiplayerServer(RequestMultiplayerServerResponse response)
    {
        connectToServer(response.IPV4Address, (ushort)response.Ports[0].Num);
    }

    private void OnRequestMultiplayerServerError(PlayFabError error)
    {
        Debug.Log(error.ErrorMessage);
    }

    private void connectToServer(string address, ushort port)
    {
        Debug.Log("Connecting to " + address + ":" + port);
        networkDriver = NetworkDriver.Create();
        networkConnection = default(NetworkConnection);

        var endpoint = NetworkEndPoint.Parse(address, port);
        networkConnection = networkDriver.Connect(endpoint);
        startedConnectionRequest = true;

        enemyManager = FindObjectOfType<EnemyManager>();
        enemies = GameObject.FindGameObjectsWithTag("Enemy");
        Debug.Log("Detected " + enemies.Length + " enemies");
        // Sort the array by name to keep it consistent across clients
        Array.Sort(enemies, (e1, e2) => e1.name.CompareTo(e2.name));

        int length = enemies.Length;
        enemyStatus = new byte[length];
        for (var i = 0; i < length; i++)
        {
            if (enemies[i] != null)
            {
                enemyStatus[i] = (byte)(enemies[i].activeSelf ? 1 : 0);
            }
        }
    }

    public void OnDestroy()
    {
        networkDriver.Dispose();
    }

    private void ReportStatBotDestroyed()
    {
        UpdatePlayerStatisticsRequest requestData = new UpdatePlayerStatisticsRequest()
        {
            Statistics = new List<StatisticUpdate>() {
            new StatisticUpdate() { StatisticName = "Bot's destroyed", Value = 1 }
        }
        };
        PlayFabClientAPI.UpdatePlayerStatistics(requestData, OnReportStatBotDestroyedResult, OnReportStatBotDestroyedError);
    }


    private void OnReportStatBotDestroyedResult(UpdatePlayerStatisticsResult response)
    {
        // Successfully Reported
    }


    private void OnReportStatBotDestroyedError(PlayFabError error)
    {
        Debug.Log(error.ErrorMessage);
    }

    private void GetLeaderboardData()
    {
        GetLeaderboardRequest requestData = new GetLeaderboardRequest()
        {
            StatisticName = "Bot's destroyed",
            StartPosition = 0,
            MaxResultsCount = 100,
        };
        PlayFabClientAPI.GetLeaderboard(requestData, OnGetLeaderboardDataResult, OnGetLeaderboardDataError);
    }

    private void OnGetLeaderboardDataResult(GetLeaderboardResult response)
    {
        foreach (var entry in response.Leaderboard)
        {
            Debug.Log(entry.Position + ". " + entry.DisplayName + " : " + entry.StatValue);
        }
    }

    private void OnGetLeaderboardDataError(PlayFabError error)
    {
        Debug.Log(error.ErrorMessage);
    }

    // Start is called before the first frame update
    void Start()
    {
        Debug.Log("Starting Client");
        if (RunLocal)
        {
            connectToServer("127.0.0.1", 7777);
        }
        else
        {
            connectToServer(matchAddress, matchPort);
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (!startedConnectionRequest)
        {
            return;
        }

        networkDriver.ScheduleUpdate().Complete();

        if (!networkConnection.IsCreated)
        {
            if (!isDone)
            {
                Debug.Log("Something went wrong during connect");
            }
            return;
        }

        DataStreamReader stream;
        NetworkEvent.Type cmd;

        if (!isConnected)
        {
            Debug.Log("Connecting...");
        }

        while ((cmd = networkConnection.PopEvent(networkDriver, out stream)) != NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Connect)
            {
                Debug.Log("We are now connected to the server");
                isConnected = true;
            }
            else if (cmd == NetworkEvent.Type.Data)
            {
                uint value = stream.ReadUInt();
                if (value == enemyStatus.Length) // Make sure the enemy length is consistent
                {
                    for (int b = 0; b < enemyStatus.Length; b++)
                    {
                        byte isAlive = stream.ReadByte();
                        if (enemyStatus[b] > 0 && isAlive == 0) // Enemy is alive locally but dead on the server
                        {
                            Debug.Log("enemy " + b + " dead");
                            // Find the right enemy and "kill" it for the animation
                            foreach (var en in enemyManager.Enemies)
                            {
                                if (en.name == enemies[b].name)
                                {
                                    en.m_Health.Kill();
                                    break;
                                }
                            }
                            enemyStatus[b] = 0;
                        }
                    }
                }
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {
                Debug.Log("Client got disconnected from server");
                networkConnection = default(NetworkConnection);
                isConnected = false;
            }
        }

        // Update the status with local game state
        for (var i = 0; i < enemies.Length; i++)
        {
            if (enemyStatus[i] > 0 &&
                (enemies[i] == null || !enemies[i].activeSelf))
            {
                enemyStatus[i] = 0;
                ReportStatBotDestroyed();
            }
        }

        // Send latest status to the server
        if (isConnected)
        {
            networkDriver.BeginSend(networkConnection, out var writer);
            writer.WriteUInt((uint)enemyStatus.Length);
            for (int b = 0; b < enemyStatus.Length; b++)
            {
                writer.WriteByte(enemyStatus[b]);
            }
            networkDriver.EndSend(writer);
        }
    }
}
