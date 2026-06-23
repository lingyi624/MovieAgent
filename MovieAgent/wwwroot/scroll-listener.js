export function setupScrollListener(element, dotNetRef) {
    element.addEventListener('scroll', () => {
        const scrollTop = element.scrollTop;
        const scrollHeight = element.scrollHeight;
        const clientHeight = element.clientHeight;
        
        if (scrollTop + clientHeight >= scrollHeight - 200) {
            dotNetRef.invokeMethodAsync('OnScrollNearBottom');
        }
    });
}

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}