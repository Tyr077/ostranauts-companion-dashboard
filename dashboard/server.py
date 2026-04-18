"""
Ostranauts Companion Dashboard Server
Proxies game API data, caches history, serves Matrix-themed web dashboard.
"""
import json
import threading
import time
from collections import deque
from datetime import datetime

import requests
from flask import Flask, jsonify, render_template, request

# --- Configuration ---
GAME_API = "http://localhost:8085"
DASHBOARD_PORT = 8086
POLL_INTERVAL = 0.5  # seconds between game API polls
MAX_HISTORY = 600    # ~5 minutes of data at 0.5s intervals
MAX_EVENTS = 200

app = Flask(__name__)

# --- Shared State (thread-safe via GIL for simple reads/writes) ---
game_status = {}
game_crew = {}
game_ship = {}
game_events = []
game_nav = {}
crew_history = {}       # {crew_id: deque of {timestamp, stats}}
connection_ok = False
last_poll_time = None


def poll_game_api():
    """Background thread: polls game CompanionServer API and caches results."""
    global game_status, game_crew, game_ship, game_events, game_nav
    global connection_ok, last_poll_time

    while True:
        try:
            # Fetch all endpoints
            status_r = requests.get(f"{GAME_API}/api/status", timeout=2)
            crew_r = requests.get(f"{GAME_API}/api/crew", timeout=2)
            ship_r = requests.get(f"{GAME_API}/api/ship", timeout=2)
            events_r = requests.get(f"{GAME_API}/api/events", timeout=2)

            game_status = status_r.json()
            crew_data = crew_r.json()
            game_crew = crew_data
            game_ship = ship_r.json()
            game_events = events_r.json()

            connection_ok = True
            last_poll_time = datetime.now().strftime("%H:%M:%S")

            # Build crew history for trend tracking
            if "crew" in crew_data:
                now = time.time()
                for member in crew_data["crew"]:
                    cid = member.get("id", "?")
                    if cid not in crew_history:
                        crew_history[cid] = deque(maxlen=MAX_HISTORY)
                    crew_history[cid].append({
                        "t": now,
                        "stats": member.get("stats", {}),
                    })

        except requests.exceptions.ConnectionError:
            connection_ok = False
        except Exception as e:
            connection_ok = False
            print(f"[poll] Error: {e}")

        time.sleep(POLL_INTERVAL)


# --- Routes ---

@app.route("/")
def index():
    return render_template("index.html")


@app.route("/api/state")
def api_state():
    """Combined endpoint: everything the dashboard needs in one call."""
    # Build alerts from crew conditions
    alerts = []
    if "crew" in game_crew:
        for member in game_crew["crew"]:
            name = member.get("name", "?")
            conditions = member.get("conditions", [])
            for cond in conditions:
                if cond in ("DcOxygen02", "DcOxygen03", "DcOxygen04", "Gasping",
                            "AttemptingPledgeSurviveO2"):
                    alerts.append({"crew": name, "type": "critical",
                                   "msg": f"O2 CRITICAL: {cond}"})
                elif "CO2Poisoning" in cond and int(cond[-2:]) >= 4:
                    alerts.append({"crew": name, "type": "critical",
                                   "msg": f"CO2 POISONING: {cond}"})
                elif cond == "IsUnconscious":
                    alerts.append({"crew": name, "type": "critical",
                                   "msg": "UNCONSCIOUS"})
                elif cond in ("DcFatigue03", "DcSleep03"):
                    alerts.append({"crew": name, "type": "warning",
                                   "msg": f"Exhaustion: {cond}"})

    return jsonify({
        "connected": connection_ok,
        "lastPoll": last_poll_time,
        "status": game_status,
        "crew": game_crew.get("crew", []),
        "ship": game_ship,
        "events": game_events[-50:],  # Last 50 events
        "alerts": alerts,
    })


@app.route("/api/nav")
def api_nav():
    """Navigation data — pass-through proxy, no caching. Each request forwards
    directly to the plugin, so remote browsers get the plugin's full 12Hz rate
    instead of the 500ms cached poll loop."""
    try:
        r = requests.get(f"{GAME_API}/api/nav", timeout=1)
        return r.content, r.status_code, {"Content-Type": "application/json"}
    except Exception:
        return jsonify({})


@app.route("/api/history/<crew_id>")
def api_history(crew_id):
    """Crew stat history for trend graphs."""
    if crew_id not in crew_history:
        return jsonify([])
    history = list(crew_history[crew_id])
    return jsonify(history)


# --- Startup ---

if __name__ == "__main__":
    print(f"Ostranauts Companion Dashboard")
    print(f"Game API: {GAME_API}")
    print(f"Dashboard: http://localhost:{DASHBOARD_PORT}")
    print()

    # Start background poller
    poller = threading.Thread(target=poll_game_api, daemon=True)
    poller.start()

    app.run(host="0.0.0.0", port=DASHBOARD_PORT, debug=False)
