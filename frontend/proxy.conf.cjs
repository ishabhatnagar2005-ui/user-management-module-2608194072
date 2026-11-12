// Keep the API host in development infrastructure, not the shipped client.
module.exports = { '/api': { target: process.env.API_PROXY_TARGET || 'http://127.0.0.1:5088', secure: false, changeOrigin: true } };
