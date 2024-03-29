window.hljs?.highlightAll()

if (!localStorage.getItem('data:tags.txt'))
{
    fetch('/data/tags.txt')
        .then(r => r.text())
        .then(txt => localStorage.setItem('data:tags.txt', txt));
}

function metadataDate(metadataJson) {
    try {
        if (metadataJson) {
            return new Date(parseInt(metadataJson.match(/Date\((\d+)\)/)[1]))
        }
    } catch{}
    return new Date() - (24 * 60 * 60 * 1000) 
}

const metadataJson = localStorage.getItem('/metadata/app.json')
const oneHourAgo = new Date() - 60 * 60 * 1000
const clearMetadata = !metadataJson
    || location.search.includes('clear=metadata')
    || metadataDate(metadataJson) < oneHourAgo 

if (clearMetadata) {
    fetch('/metadata/app.json')
        .then(r => r.text())
        .then(json => {
            console.log('updating /metadata/app.json...')
            localStorage.setItem('/metadata/app.json', json)
        })
}
