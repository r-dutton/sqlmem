const chartCtx = document.getElementById('sqlChart').getContext('2d');
const windowSelect = document.getElementById('window-select');
const summaryContainer = document.querySelector('#latest-summary .summary-content');
const findingsList = document.querySelector('#findings .findings-list');

const chart = new Chart(chartCtx, {
    type: 'line',
    data: {
        labels: [],
        datasets: [
            {
                label: 'Private (GiB)',
                data: [],
                borderColor: '#4f83ff',
                tension: 0.3,
                fill: false,
                borderWidth: 2
            },
            {
                label: 'Working Set (GiB)',
                data: [],
                borderColor: '#7dd3fc',
                tension: 0.3,
                fill: false,
                borderWidth: 2
            },
            {
                label: 'Locked/Large (GiB)',
                data: [],
                borderColor: '#f97316',
                tension: 0.3,
                fill: false,
                borderDash: [6, 6],
                borderWidth: 2
            }
        ]
    },
    options: {
        responsive: true,
        scales: {
            x: {
                ticks: {
                    maxRotation: 0,
                    autoSkip: true
                }
            },
            y: {
                beginAtZero: true,
                title: {
                    display: true,
                    text: 'GiB'
                }
            }
        },
        plugins: {
            legend: {
                labels: {
                    usePointStyle: true
                }
            }
        }
    }
});

async function fetchJson(url) {
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error(`Failed to fetch ${url}: ${response.status}`);
    }
    return await response.json();
}

function bytesToGiB(value) {
    return value / 1024 / 1024 / 1024;
}

async function refreshChart() {
    const hours = parseInt(windowSelect.value, 10);
    const series = await fetchJson(`/api/timeseries/sql?hours=${hours}`);

    chart.data.labels = series.map(point => new Date(point.timestamp).toLocaleString());
    chart.data.datasets[0].data = series.map(point => bytesToGiB(point.privateBytes));
    chart.data.datasets[1].data = series.map(point => bytesToGiB(point.workingSetBytes));
    chart.data.datasets[2].data = series.map(point => bytesToGiB(point.lockedBytes + point.largePageBytes));

    chart.update();
}

function formatGiB(bytes) {
    return `${bytesToGiB(bytes).toFixed(1)} GiB`;
}

async function refreshSummary() {
    const snapshot = await fetchJson('/api/snapshots/latest');
    if (!snapshot) {
        summaryContainer.innerHTML = '<p>No snapshots captured yet.</p>';
        return;
    }

    const lines = [
        `<dt>Captured</dt><dd>${new Date(snapshot.timestamp).toLocaleString()}</dd>`,
        `<dt>Total Physical</dt><dd>${formatGiB(snapshot.totalPhysicalBytes)}</dd>`,
        `<dt>Available</dt><dd>${formatGiB(snapshot.availablePhysicalBytes)}</dd>`,
        `<dt>Kernel NP/P</dt><dd>${formatGiB(snapshot.kernelNonPagedBytes)} / ${formatGiB(snapshot.kernelPagedBytes)}</dd>`,
        `<dt>System Cache</dt><dd>${formatGiB(snapshot.systemCacheBytes)}</dd>`
    ];

    const topProcesses = snapshot.topProcesses
        .map(proc => `<li>${proc.imageName} (PID ${proc.pid}) — Private ${formatGiB(proc.privateBytes)} (${formatGiB(proc.lockedBytes)} locked)</li>`)
        .join('');

    summaryContainer.innerHTML = `
        <dl>${lines.join('')}</dl>
        <h3>Top Processes</h3>
        <ul class="top-processes">${topProcesses || '<li>No processes recorded.</li>'}</ul>
    `;
}

async function refreshFindings() {
    const findings = await fetchJson('/api/findings');
    findingsList.innerHTML = findings.map(finding => {
        return `<li><strong>${finding.title}</strong><br><small>${new Date(finding.timestamp).toLocaleString()} — ${finding.id}</small><p>${finding.description}</p></li>`;
    }).join('') || '<li>No recent findings.</li>';
}

async function refreshAll() {
    try {
        await Promise.all([refreshChart(), refreshSummary(), refreshFindings()]);
    } catch (error) {
        console.error('Failed to refresh dashboard', error);
    }
}

windowSelect.addEventListener('change', () => {
    refreshChart().catch(err => console.error(err));
});

refreshAll();
setInterval(refreshAll, 60_000);
