using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace enemyradar
{
    // Duckov 로더가 찾는 엔트리 포인트:
    //   enemyradar.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject go = new GameObject("EnemyRadarRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);

                go.AddComponent<EnemyRadarHUD>();

                Debug.Log("[EnemyRadar] ModBehaviour.OnAfterSetup - HUD 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[EnemyRadar] 초기화 예외: " + ex);
            }
        }
    }

    internal class EnemyRadarHUD : MonoBehaviour
    {
        // ===== 플레이어 / 적 추적 =====
        private Transform _player;
        private readonly List<Transform> _enemies = new List<Transform>();

        private float _nextScanTime;
        private const float ScanInterval = 3f;

        private bool _hasTarget;
        private Transform _nearestEnemy;
        private float _nearestDist;
        private float _normalizedDist;
        private float _enemyAngleDeg; // (디버그용) 가장 가까운 적 방향

        // ===== 거리 기준 (미터) =====
        // 0 ~ 7m    -> 2번 링 (가까움, 빨간 도넛 링)
        // 7 ~ 20m   -> 3번 링 (중간, 실선 피자조각)
        // 20 ~ 35m  -> 4번 링 (멀다, 점선 아크)
        private float _ring2DistanceMax = 7f;
        private float _ring3DistanceMax = 20f;
        private float _ring4DistanceMax = 35f;
        private float _maxRadarDistance = 35f;

        // ===== 텍스처 =====
        private Texture2D _radarTexture;   // 배경 원 + 외곽선
        private Texture2D _ring2Texture;   // 2번: 빨간 도넛 링
        private Texture2D _ring3Texture;   // 3번: 실선 피자조각
        private Texture2D _ring4Texture;   // 4번: 점선 아크
        private bool _texReady;

        // ===== 디버그 텍스트 스타일 =====
        private GUIStyle _labelStyle;
        private bool _styleReady;

        // ===== team 필드 캐시 =====
        private readonly Dictionary<Type, FieldInfo> _teamFieldCache = new Dictionary<Type, FieldInfo>();

        // ===== 다수 각도 캐시 (3번/4번 링용) =====
        private readonly List<float> _midAngles = new List<float>();
        private readonly List<float> _farAngles = new List<float>();

        // Transform 요약 정보
        private class CharacterInfo
        {
            public Transform Tr;
            public string Team;
            public bool HasTeam;
            public bool IsPlayerTeam;
            public bool IsEnemyTeam;
        }

        private void Start()
        {
            Debug.Log("[EnemyRadarHUD] Start - 준비 완료");
        }

        private void Update()
        {
            if (Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanInterval;
                ScanCharacters();
            }

            UpdateNearestEnemy();
        }

        private void OnGUI()
        {
            if (!_texReady)
                BuildTextures();
            if (!_styleReady)
                BuildStyle();
            if (_radarTexture == null)
                return;

            float size   = 200f;
            float margin = 20f;

            // ↓ 레이더 위치: 원래 쓰던 오른쪽 아래 (살짝 위로)
            float radarX = Screen.width  - size - margin;
            float radarY = Screen.height - size - margin - 80f;

            Rect radarRect = new Rect(radarX, radarY, size, size);

            Color prevColor = GUI.color;

            // 1) 레이더 배경
            GUI.color = Color.white;
            GUI.DrawTexture(radarRect, _radarTexture);

            // 2) 링 표시 (다수 적 지원)
            if (_player != null && _enemies.Count > 0)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
                float alpha = Mathf.Lerp(0.5f, 1.0f, pulse);
                GUI.color   = new Color(1f, 1f, 1f, alpha);

                Vector3 playerPos = _player.position;

                // 플레이어/카메라 전방
                Vector3 fwd;
                if (Camera.main != null)
                    fwd = Camera.main.transform.forward;
                else
                    fwd = _player.forward;

                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f)
                    fwd = Vector3.forward;
                fwd.Normalize();
                float fwdAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

                bool hasRing2 = false;
                _midAngles.Clear();
                _farAngles.Clear();

                // 각 적 위치에 따라 링 분류
                for (int i = 0; i < _enemies.Count; i++)
                {
                    Transform tr = _enemies[i];
                    if (tr == null) continue;

                    Vector3 toEnemy = tr.position - playerPos;
                    float distance = toEnemy.magnitude;

                    if (distance <= _ring2DistanceMax)
                    {
                        // 2번 영역(가까움) 존재 여부만 체크
                        hasRing2 = true;
                        continue;
                    }

                    if (distance <= _ring3DistanceMax)
                    {
                        // 3번 영역(중간) → 실선 피자조각 여러 개
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude < 0.0001f)
                            continue;
                        toEnemy.Normalize();

                        float enAngle = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                        float rel = Mathf.DeltaAngle(fwdAngle, enAngle); // -180~180, 앞이 0
                        _midAngles.Add(rel);
                    }
                    else if (distance <= _ring4DistanceMax)
                    {
                        // 4번 영역(멀다) → 점선 아크 여러 개
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude < 0.0001f)
                            continue;
                        toEnemy.Normalize();

                        float enAngle = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                        float rel = Mathf.DeltaAngle(fwdAngle, enAngle);
                        _farAngles.Add(rel);
                    }
                }

                // 2번 링: 근접 적이 하나라도 있으면 중앙에 도넛 한 개
                if (hasRing2 && _ring2Texture != null)
                {
                    GUI.DrawTexture(radarRect, _ring2Texture);
                }

                // 3번 링: 중거리 적들 전부 방향별로 피자조각
                if (_ring3Texture != null)
                {
                    for (int i = 0; i < _midAngles.Count; i++)
                    {
                        float angle = _midAngles[i];
                        Matrix4x4 prevMatrix = GUI.matrix;
                        Vector2 pivot = new Vector2(
                            radarRect.x + radarRect.width * 0.5f,
                            radarRect.y + radarRect.height * 0.5f);

                        GUIUtility.RotateAroundPivot(angle, pivot);
                        GUI.DrawTexture(radarRect, _ring3Texture);
                        GUI.matrix = prevMatrix;
                    }
                }

                // 4번 링: 먼 적들 방향별 점선 아크 (조금 더 짧은 아크)
                if (_ring4Texture != null)
                {
                    for (int i = 0; i < _farAngles.Count; i++)
                    {
                        float angle = _farAngles[i];
                        Matrix4x4 prevMatrix = GUI.matrix;
                        Vector2 pivot = new Vector2(
                            radarRect.x + radarRect.width * 0.5f,
                            radarRect.y + radarRect.height * 0.5f);

                        GUIUtility.RotateAroundPivot(angle, pivot);
                        GUI.DrawTexture(radarRect, _ring4Texture);
                        GUI.matrix = prevMatrix;
                    }
                }

                GUI.color = prevColor;
            }
            else
            {
                GUI.color = prevColor;
            }

            // 3) 디버그 텍스트 (레이더 왼쪽)
            Rect textRect = new Rect(radarRect.x - 220f, radarRect.y, 210f, radarRect.height);

            if (_hasTarget && _nearestEnemy != null)
            {
                string ringText = "4번(멀다)";
                float d = _nearestDist;
                if (d <= _ring2DistanceMax) ringText = "2번(가까움)";
                else if (d <= _ring3DistanceMax) ringText = "3번(중간)";

                string info =
                    "타깃: " + SafeGetName(_nearestEnemy) + "\n" +
                    "적 수: " + _enemies.Count + "\n" +
                    "거리: " + _nearestDist.ToString("F1") + "m\n" +
                    "링: " + ringText;

                GUI.Label(textRect, info, _labelStyle);
            }
            else
            {
                GUI.Label(textRect, "타깃 없음\n적 수: " + _enemies.Count, _labelStyle);
            }
        }

        // ================== 캐릭터 스캔 ==================

        private FieldInfo GetTeamField(Type t)
        {
            FieldInfo fi;
            if (_teamFieldCache.TryGetValue(t, out fi))
                return fi;

            fi = t.GetField("team", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _teamFieldCache[t] = fi;
            return fi;
        }

        private string GetTeamStringFromField(MonoBehaviour mb, FieldInfo fi)
        {
            try
            {
                if (mb == null || fi == null) return "";
                object val = fi.GetValue(mb);
                if (val == null) return "";
                return val.ToString();
            }
            catch
            {
                return "";
            }
        }

        private void ScanCharacters()
        {
            _player = null;
            _enemies.Clear();

            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            if (all == null || all.Length == 0)
            {
                _hasTarget = false;
                _nearestEnemy = null;
                Debug.Log("[EnemyRadarHUD] ScanCharacters - MonoBehaviour 없음");
                return;
            }

            // Transform -> CharacterInfo
            Dictionary<Transform, CharacterInfo> map = new Dictionary<Transform, CharacterInfo>();

            // pet / 환경물(브로큰월, 모래주머니, 커버, TombStone, 드럼통 등) Transform 및 그 부모 전체
            HashSet<Transform> envHierarchy = new HashSet<Transform>();

            foreach (MonoBehaviour mb in all)
            {
                if (mb == null) continue;

                Transform tr = mb.transform;
                if (tr == null) continue;

                string trNameLower = tr.name != null ? tr.name.ToLower() : "";
                string typeLower   = mb.GetType().Name.ToLower();

                bool isPetHere = false;
                bool isEnvHere = false; // BrokenWall / Sandbag / Cover / TombStone / Barrel 등

                // 이름/타입에 pet
                if (trNameLower.Contains("pet") || typeLower.Contains("pet"))
                {
                    isPetHere = true;
                }

                // 이름/타입으로 환경물 판정
                if (trNameLower.Contains("brokenwall")        ||
                    trNameLower.Contains("breakablewall")     ||
                    trNameLower.Contains("sandbag")           ||
                    trNameLower.Contains("sand bag")          ||
                    trNameLower.Contains("coverwall")         ||
                    trNameLower.Contains("cover_wall")        ||
                    trNameLower.Contains("cover")             ||
                    trNameLower.Contains("barricade")         ||
                    trNameLower.Contains("tombstone")         ||
                    trNameLower.Contains("explosive_oilbarrel_25") ||
                    trNameLower.Contains("explosive_oilbarrel")    ||
                    trNameLower.Contains("oilbarrel")         ||
                    trNameLower.Contains("testhalfobsticle_18")    ||
                    trNameLower.Contains("halfobsticle")      ||
                    trNameLower.Contains("obsticle")          ||
                    typeLower.Contains("brokenwall")          ||
                    typeLower.Contains("breakablewall")       ||
                    typeLower.Contains("sandbag")             ||
                    typeLower.Contains("sand bag")            ||
                    typeLower.Contains("coverwall")           ||
                    typeLower.Contains("cover_wall")          ||
                    typeLower.Contains("cover")               ||
                    typeLower.Contains("barricade")           ||
                    typeLower.Contains("tombstone")           ||
                    typeLower.Contains("explosive_oilbarrel_25") ||
                    typeLower.Contains("explosive_oilbarrel")    ||
                    typeLower.Contains("oilbarrel")           ||
                    typeLower.Contains("testhalfobsticle_18") ||
                    typeLower.Contains("halfobsticle")        ||
                    typeLower.Contains("obsticle"))
                {
                    isEnvHere = true;
                }

                FieldInfo tf = GetTeamField(mb.GetType());
                string teamStr = "";
                if (tf != null)
                {
                    teamStr = GetTeamStringFromField(mb, tf);
                    string lowerTeam = string.IsNullOrEmpty(teamStr) ? "" : teamStr.ToLower();

                    if (lowerTeam.Contains("pet"))
                        isPetHere = true;

                    if (lowerTeam.Contains("brokenwall")        ||
                        lowerTeam.Contains("breakablewall")     ||
                        lowerTeam.Contains("sandbag")           ||
                        lowerTeam.Contains("sand bag")          ||
                        lowerTeam.Contains("cover")             ||
                        lowerTeam.Contains("barricade")         ||
                        lowerTeam.Contains("tombstone")         ||
                        lowerTeam.Contains("explosive_oilbarrel_25") ||
                        lowerTeam.Contains("explosive_oilbarrel")    ||
                        lowerTeam.Contains("oilbarrel")         ||
                        lowerTeam.Contains("testhalfobsticle_18")    ||
                        lowerTeam.Contains("halfobsticle")      ||
                        lowerTeam.Contains("obsticle"))
                    {
                        isEnvHere = true;
                    }
                }

                // 펫 / 환경물(모래주머니, 커버, 브로큰월, TombStone, Barrel 등)은
                // 트랜스폼 + 부모 전부 무시 목록에 포함
                if (isPetHere || isEnvHere)
                {
                    Transform p = tr;
                    while (p != null)
                    {
                        if (!envHierarchy.Add(p))
                            break;
                        p = p.parent;
                    }
                }

                if (tf == null)
                    continue;

                CharacterInfo info;
                if (!map.TryGetValue(tr, out info))
                {
                    info = new CharacterInfo { Tr = tr };
                    map.Add(tr, info);
                }

                if (!string.IsNullOrEmpty(teamStr))
                {
                    info.Team = teamStr;
                    info.HasTeam = true;

                    string lowerT = teamStr.ToLower();

                    // 플레이어 팀
                    if (lowerT.Contains("player"))
                        info.IsPlayerTeam = true;

                    // 아군/중립 팀 추정 (이 문자열 포함하면 적 아님)
                    bool isAllyOrNeutral = false;
                    if (lowerT.Contains("ally")      ||
                        lowerT.Contains("friend")    ||
                        lowerT.Contains("friendly")  ||
                        lowerT.Contains("neutral")   ||
                        lowerT.Contains("civil")     ||
                        lowerT.Contains("shop")      ||
                        lowerT.Contains("vendor")    ||
                        lowerT.Contains("test")      ||
                        lowerT.Contains("dummy")     ||
                        lowerT.Contains("training"))
                    {
                        isAllyOrNeutral = true;
                    }

                    // 플레이어 팀도 아니고, 아군/중립도 아니면 "적 팀"으로 취급
                    if (!info.IsPlayerTeam && !isAllyOrNeutral)
                    {
                        info.IsEnemyTeam = true;
                    }
                }
            }

            // 후보 필터링: team 없는 애, 펫/환경물 계열은 제거
            List<CharacterInfo> chars = new List<CharacterInfo>();
            foreach (KeyValuePair<Transform, CharacterInfo> kv in map)
            {
                CharacterInfo info = kv.Value;
                if (!info.HasTeam) continue;
                if (info.Tr == null) continue;
                if (envHierarchy.Contains(info.Tr)) continue; // 펫 + 환경물 제거

                chars.Add(info);
            }

            if (chars.Count == 0)
            {
                _hasTarget = false;
                _nearestEnemy = null;
                Debug.Log("[EnemyRadarHUD] ScanCharacters - 유효 캐릭터 없음 (펫/환경물만 있거나 team 없음)");
                return;
            }

            // ───── 플레이어 Transform 결정 ─────
            Transform camTr = Camera.main != null ? Camera.main.transform : null;

            CharacterInfo playerInfo = null;
            float bestPlayerDist = float.MaxValue;

            // 1) team 에 player 가 들어있는 애들 중 카메라와 가장 가까운 것
            foreach (CharacterInfo info in chars)
            {
                if (!info.IsPlayerTeam) continue;

                float d;
                if (camTr != null)
                    d = Vector3.Distance(camTr.position, info.Tr.position);
                else
                    d = Vector3.Distance(Vector3.zero, info.Tr.position);

                if (d < bestPlayerDist)
                {
                    bestPlayerDist = d;
                    playerInfo = info;
                }
            }

            // 2) 위에서 못 찾았으면 그냥 카메라에 제일 가까운 애를 플레이어로
            if (playerInfo == null)
            {
                foreach (CharacterInfo info in chars)
                {
                    float d;
                    if (camTr != null)
                        d = Vector3.Distance(camTr.position, info.Tr.position);
                    else
                        d = Vector3.Distance(Vector3.zero, info.Tr.position);

                    if (d < bestPlayerDist)
                    {
                        bestPlayerDist = d;
                        playerInfo = info;
                    }
                }
            }

            _player = playerInfo != null ? playerInfo.Tr : null;

            // ───── 적 리스트 = "적 팀" + 플레이어 제외 ─────
            foreach (CharacterInfo info in chars)
            {
                if (info.Tr == null) continue;
                if (_player != null && info.Tr == _player) continue;
                if (!info.IsEnemyTeam) continue;

                _enemies.Add(info.Tr);
            }

            Debug.Log("[EnemyRadarHUD] ScanCharacters - 후보=" +
                      chars.Count + ", 펫/환경물 제외후 적수=" + _enemies.Count +
                      ", player=" + SafeGetName(_player));
        }

        private void UpdateNearestEnemy()
        {
            _hasTarget = false;
            _nearestEnemy = null;
            _nearestDist = 0f;
            _normalizedDist = 1f;
            _enemyAngleDeg = 0f;

            if (_player == null || _enemies.Count == 0)
                return;

            Vector3 playerPos = _player.position;
            float best = float.MaxValue;
            Transform bestEnemy = null;

            for (int i = 0; i < _enemies.Count; i++)
            {
                Transform tr = _enemies[i];
                if (tr == null) continue;

                float d = Vector3.Distance(playerPos, tr.position);
                if (d < best)
                {
                    best = d;
                    bestEnemy = tr;
                }
            }

            if (bestEnemy != null)
            {
                _hasTarget = true;
                _nearestEnemy = bestEnemy;
                _nearestDist = best;

                _maxRadarDistance = _ring4DistanceMax;
                if (_maxRadarDistance <= 0f) _maxRadarDistance = 1f;
                _normalizedDist = Mathf.Clamp01(_nearestDist / _maxRadarDistance);

                // (디버그용) 가장 가까운 적 방향도 계산해 둔다
                Vector3 toEnemy = bestEnemy.position - playerPos;
                toEnemy.y = 0f;
                if (toEnemy.sqrMagnitude > 0.0001f)
                {
                    Vector3 fwd;
                    if (Camera.main != null)
                        fwd = Camera.main.transform.forward;
                    else
                        fwd = _player.forward;

                    fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.0001f)
                        fwd = Vector3.forward;

                    toEnemy.Normalize();
                    fwd.Normalize();

                    float fwdAngle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    float enAngle  = Mathf.Atan2(toEnemy.x, toEnemy.z) * Mathf.Rad2Deg;
                    float rel      = Mathf.DeltaAngle(fwdAngle, enAngle);

                    _enemyAngleDeg = rel;
                }
            }
        }

        private string SafeGetName(Transform tr)
        {
            if (tr == null) return "(null)";
            string n = tr.name;
            return string.IsNullOrEmpty(n) ? "(noname)" : n;
        }

        // ================== 텍스처 / 스타일 빌드 ==================

        private void BuildTextures()
        {
            int size = 256;

            _radarTexture = BuildRadarBackground(size);

            Color red = new Color(1f, 0.1f, 0.1f, 0.95f);

            // 2번 링: 빨간 도넛 "원형 링"
            _ring2Texture = BuildSolidRingTexture(
                size,
                0.20f, 0.38f,
                red);

            // 3번 링: 방향 실선 피자조각 (원호 일부만 채움)
            _ring3Texture = BuildSolidArcTexture(
                size,
                0.50f, 0.56f,
                40f,
                red);

            // 4번 링: 바깥 점선 아크 (조금 더 짧게: halfAngle 45)
            _ring4Texture = BuildSegmentedArcTexture(
                size,
                0.80f, 0.88f,
                45f,      // <- 80 → 60 → 45로 줄인 값 (조금 더 짧게)
                9,
                0.65f,
                red);

            _texReady = true;
        }

        private void BuildStyle()
        {
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 18;
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.alignment = TextAnchor.UpperLeft;
            _styleReady = true;
        }

        private Texture2D BuildRadarBackground(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float outerRadius   = size * 0.48f;
            float outerRadiusSq = outerRadius * outerRadius;

            Color bgInner  = new Color(0.9f, 0.9f, 0.9f, 0.03f);
            Color bgOuter  = new Color(0f, 0f, 0f, 0.55f);
            Color ringEdge = new Color(1f, 0.6f, 0.1f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq > outerRadiusSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float dist = Mathf.Sqrt(distSq);
                    float t = dist / outerRadius;

                    Color c = Color.Lerp(bgInner, bgOuter, t * t);

                    float borderWidth = 2f;
                    if (Mathf.Abs(dist - outerRadius) <= borderWidth)
                    {
                        c = ringEdge;
                    }

                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            return tex;
        }

        // 2번 링용: 전체 "도넛 링"
        private Texture2D BuildSolidRingTexture(
            int size,
            float innerRatio,
            float outerRatio,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq >= innerSq && distSq <= outerSq)
                    {
                        tex.SetPixel(x, y, color);
                    }
                    else
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    }
                }
            }

            tex.Apply();
            return tex;
        }

        // 3번 링용: 실선 피자조각 아크
        private Texture2D BuildSolidArcTexture(
            int size,
            float innerRatio,
            float outerRatio,
            float halfAngleDeg,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            float centerAngle = 90f; // 위쪽(12시 방향)

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < innerSq || distSq > outerSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;

                    float delta = Mathf.DeltaAngle(angle, centerAngle);

                    // halfAngleDeg 안쪽만 칠해서 "조각" 모양
                    if (Mathf.Abs(delta) <= halfAngleDeg)
                        tex.SetPixel(x, y, color);
                    else
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }

        // 4번 링용: 점선 반원 아크
        private Texture2D BuildSegmentedArcTexture(
            int size,
            float innerRatio,
            float outerRatio,
            float halfAngleDeg,
            int segmentCount,
            float fillRatio,
            Color color)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;

            float radarOuter = size * 0.48f;
            float innerR = radarOuter * innerRatio;
            float outerR = radarOuter * outerRatio;

            float innerSq = innerR * innerR;
            float outerSq = outerR * outerR;

            float centerAngle = 90f; // 위쪽(12시 방향)
            float totalAngle  = halfAngleDeg * 2f;

            if (segmentCount <= 0) segmentCount = 1;
            if (fillRatio   <= 0f) fillRatio   = 0.01f;
            if (fillRatio   >  1f) fillRatio   = 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < innerSq || distSq > outerSq)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;

                    float delta = Mathf.DeltaAngle(angle, centerAngle);
                    if (Mathf.Abs(delta) > halfAngleDeg)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                        continue;
                    }

                    // [-halfAngle, +halfAngle] -> [0, 1]
                    float t = (delta + halfAngleDeg) / totalAngle;

                    float scaled = t * segmentCount;
                    int segIdx = Mathf.FloorToInt(scaled);
                    if (segIdx >= segmentCount) segIdx = segmentCount - 1;

                    float inSeg = scaled - segIdx; // 0~1

                    if (inSeg <= fillRatio)
                        tex.SetPixel(x, y, color);
                    else
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                }
            }

            tex.Apply();
            return tex;
        }
    }
}
